using System.Net;

namespace HttpLogger.Core;

public class HttpMessageHandler : IDisposable
{
    private readonly ProxyConfiguration _config;
    private readonly RequestResponseLogger _logger;
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public HttpMessageHandler(ProxyConfiguration config, RequestResponseLogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var handler = new HttpClientHandler()
        {
            UseCookies = false,
            UseProxy = false,
            MaxConnectionsPerServer = 10,
            Proxy = null
        };

        if (_config.IgnoreSslErrors)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = _config.Timeout,
            DefaultRequestHeaders = 
            {
                ConnectionClose = false
            }
        };

        _httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
        _httpClient.DefaultRequestHeaders.Add("Keep-Alive", $"timeout={_config.TimeoutSeconds}, max=100");
    }

    public async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        
        var requestId = Guid.NewGuid().ToString("N")[..8];
        var timestamp = DateTime.Now;

        using var clientDisconnectCts = new CancellationTokenSource();
        using var timeoutCts = new CancellationTokenSource(_config.Timeout);
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            clientDisconnectCts.Token, 
            timeoutCts.Token
        );
        var cancellationToken = combinedCts.Token;

        // Monitor client connection in background
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!IsClientConnected(context.Response))
                    {
                        _logger.LogError($"[{requestId}] Client disconnected");
                        clientDisconnectCts.Cancel();
                        break;
                    }
                    await Task.Delay(1000, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when request completes
            }
            catch (Exception ex)
            {
                _logger.LogError($"[{requestId}] Error monitoring client connection: {ex.Message}");
            }
        }, CancellationToken.None); // 使用 None 避免立即取消

        try
        {
            // Read request body with timeout
            var requestBody = await ReadRequestBodyAsync(request, cancellationToken);
            
            // Log request
            await _logger.LogRequestAsync(requestId, timestamp, request, requestBody);

            if (!IsClientConnected(context.Response))
            {
                _logger.LogError($"[{requestId}] Client disconnected before forwarding request");
                return;
            }

            // Forward request to remote server with retry logic
            var remoteResponse = await ForwardRequestWithRetryAsync(request, requestBody, requestId, cancellationToken);
            if (remoteResponse == null)
            {
                throw new HttpRequestException("Failed to get response after all retry attempts");
            }

            if (!IsClientConnected(context.Response))
            {
                _logger.LogError($"[{requestId}] Client disconnected before processing response");
                remoteResponse.Dispose();
                return;
            }

            // Choose streaming strategy based on configuration
            if (_config.EnableStreaming)
            {
                await HandleStreamingResponseAsync(requestId, timestamp, response, remoteResponse, cancellationToken);
            }
            else
            {
                await HandleBufferedResponseAsync(requestId, timestamp, response, remoteResponse, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (clientDisconnectCts.Token.IsCancellationRequested)
        {
            _logger.LogError($"[{requestId}] Request cancelled: Client disconnected");
        }
        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            _logger.LogError($"[{requestId}] Request timeout after {_config.TimeoutSeconds} seconds");
            await SendTimeoutResponse(response);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError($"[{requestId}] HTTP request timeout");
            await SendTimeoutResponse(response);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError($"[{requestId}] HTTP error: {ex.Message}");
            await SendErrorResponse(response, "Remote server error", 502);
        }
        catch (ArgumentException ex)
        {
            _logger.LogError($"[{requestId}] Argument error: {ex.Message}");
            await SendErrorResponse(response, "Invalid request parameters", 400);
        }
        catch (Exception ex)
        {
            _logger.LogError($"[{requestId}] Unexpected error: {ex.Message}");
            await SendErrorResponse(response, "Internal proxy error", 500);
        }
    }

    /// <summary>
    /// Handles response in streaming mode - data is immediately forwarded to client while being logged
    /// </summary>
    private async Task HandleStreamingResponseAsync(string requestId, DateTime timestamp, 
        HttpListenerResponse clientResponse, HttpResponseMessage remoteResponse, 
        CancellationToken cancellationToken)
    {
        try
        {
            // Set response status and headers
            SetupClientResponse(clientResponse, remoteResponse);

            var contentLength = remoteResponse.Content.Headers.ContentLength;
            var shouldLogFullContent = !contentLength.HasValue || contentLength.Value <= _config.MaxFullLogSize;

            using var responseStream = await remoteResponse.Content.ReadAsStreamAsync(cancellationToken);
            
            if (shouldLogFullContent)
            {
                // Small response: stream while logging full content
                await StreamWithFullLogging(requestId, timestamp, responseStream, clientResponse, remoteResponse, cancellationToken);
            }
            else
            {
                // Large response: stream with metadata logging only
                await StreamWithMetadataLogging(requestId, timestamp, responseStream, clientResponse, remoteResponse, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"[{requestId}] Error in streaming response: {ex.Message}");
            throw;
        }
        finally
        {
            remoteResponse.Dispose();
        }
    }

    /// <summary>
    /// Handles response in buffered mode - entire response is cached before forwarding (original behavior)
    /// </summary>
    private async Task HandleBufferedResponseAsync(string requestId, DateTime timestamp, 
        HttpListenerResponse clientResponse, HttpResponseMessage remoteResponse, 
        CancellationToken cancellationToken)
    {
        try
        {
            // Set response status and headers
            SetupClientResponse(clientResponse, remoteResponse);

            using var responseStream = await remoteResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var memoryStream = new MemoryStream();
            
            var buffer = new byte[_config.StreamBufferSize];
            int bytesRead;
            
            // Buffer entire response
            while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await memoryStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);

                if (!IsClientConnected(clientResponse))
                {
                    _logger.LogError($"[{requestId}] Client disconnected during response buffering");
                    return;
                }
            }

            var responseBody = memoryStream.ToArray();
            
            // Log the complete response
            await _logger.LogResponseAsync(requestId, timestamp, remoteResponse, responseBody);

            if (!IsClientConnected(clientResponse))
            {
                _logger.LogError($"[{requestId}] Client disconnected before sending response");
                return;
            }

            // Send buffered response to client
            if (responseBody.Length > 0)
            {
                clientResponse.ContentLength64 = responseBody.Length;
                await WriteResponseToClient(clientResponse, responseBody, requestId, cancellationToken);
            }

            clientResponse.Close();
        }
        catch (Exception ex)
        {
            _logger.LogError($"[{requestId}] Error in buffered response: {ex.Message}");
            throw;
        }
        finally
        {
            remoteResponse.Dispose();
        }
    }

    /// <summary>
    /// Streams response while logging full content (for small responses)
    /// </summary>
    private async Task StreamWithFullLogging(string requestId, DateTime timestamp,
        Stream responseStream, HttpListenerResponse clientResponse, 
        HttpResponseMessage remoteResponse, CancellationToken cancellationToken)
    {
        using var loggingBuffer = new MemoryStream();
        var buffer = new byte[_config.StreamBufferSize];
        int bytesRead;

        try
        {
            while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                // Immediately write to client (true streaming)
                await clientResponse.OutputStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                await clientResponse.OutputStream.FlushAsync(cancellationToken);
                
                // Also buffer for logging
                await loggingBuffer.WriteAsync(buffer, 0, bytesRead, cancellationToken);

                if (!IsClientConnected(clientResponse))
                {
                    _logger.LogError($"[{requestId}] Client disconnected during streaming");
                    break;
                }
            }

            // Asynchronously log the complete response without passing potentially cancelled token
            var responseBody = loggingBuffer.ToArray();
            _ = Task.Run(async () =>
            {
                try
                {
                    await _logger.LogResponseAsync(requestId, timestamp, remoteResponse, responseBody);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error logging response for {requestId}: {ex.Message}");
                }
            }, CancellationToken.None); // Use None to prevent immediate cancellation

            clientResponse.Close();
        }
        catch (IOException ex)
        {
            _logger.LogError($"[{requestId}] Client connection lost during streaming: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            _logger.LogError($"[{requestId}] Argument error during streaming: {ex.Message}");
            throw;
        }
        catch (ObjectDisposedException)
        {
            _logger.LogError($"[{requestId}] Stream disposed during streaming");
        }
    }

    /// <summary>
    /// Streams response with metadata logging only (for large responses)
    /// </summary>
    private async Task StreamWithMetadataLogging(string requestId, DateTime timestamp,
        Stream responseStream, HttpListenerResponse clientResponse, 
        HttpResponseMessage remoteResponse, CancellationToken cancellationToken)
    {
        var buffer = new byte[_config.StreamBufferSize];
        var firstChunk = new byte[Math.Min(1024, _config.StreamBufferSize)]; // Capture first 1KB
        int bytesRead;
        long totalBytes = 0;
        bool firstChunkCaptured = false;

        try
        {
            while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                // Immediately write to client (true streaming)
                await clientResponse.OutputStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                await clientResponse.OutputStream.FlushAsync(cancellationToken);
                
                // Capture first chunk for logging with proper bounds checking
                if (!firstChunkCaptured && bytesRead > 0)
                {
                    var chunkSize = Math.Min(bytesRead, firstChunk.Length);
                    if (chunkSize > 0 && buffer.Length >= chunkSize && firstChunk.Length >= chunkSize)
                    {
                        Array.Copy(buffer, 0, firstChunk, 0, chunkSize);
                        // Resize firstChunk to actual copied size
                        if (chunkSize < firstChunk.Length)
                        {
                            var actualFirstChunk = new byte[chunkSize];
                            Array.Copy(firstChunk, 0, actualFirstChunk, 0, chunkSize);
                            firstChunk = actualFirstChunk;
                        }
                        firstChunkCaptured = true;
                    }
                }

                totalBytes += bytesRead;

                if (!IsClientConnected(clientResponse))
                {
                    _logger.LogError($"[{requestId}] Client disconnected during streaming");
                    break;
                }
            }

            // Asynchronously log response metadata without passing potentially cancelled token
            _ = Task.Run(async () =>
            {
                try
                {
                    await _logger.LogResponseMetadataAsync(requestId, timestamp, remoteResponse, 
                                      firstChunk, totalBytes);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error logging response metadata for {requestId}: {ex.Message}");
                }
            }, CancellationToken.None); // Use None to prevent immediate cancellation

            clientResponse.Close();
        }
        catch (IOException ex)
        {
            _logger.LogError($"[{requestId}] Client connection lost during streaming: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            _logger.LogError($"[{requestId}] Argument error during metadata streaming: {ex.Message}");
            throw;
        }
        catch (ObjectDisposedException)
        {
            _logger.LogError($"[{requestId}] Stream disposed during streaming");
        }
    }


    private void SetupClientResponse(HttpListenerResponse clientResponse, HttpResponseMessage remoteResponse)
    {
        if (clientResponse == null)
            throw new ArgumentNullException(nameof(clientResponse));
        if (remoteResponse == null)
            throw new ArgumentNullException(nameof(remoteResponse));

        clientResponse.StatusCode = (int)remoteResponse.StatusCode;
        clientResponse.StatusDescription = remoteResponse.ReasonPhrase ?? string.Empty;
        clientResponse.KeepAlive = true;

        // Copy response headers
        foreach (var header in remoteResponse.Headers)
        {
            if (IsHopByHopHeader(header.Key))
                continue;

            try
            {
                if (header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                {
                    clientResponse.Headers.Add("Connection", "keep-alive");
                }
                else
                {
                    clientResponse.Headers.Add(header.Key, string.Join(", ", header.Value));
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail for invalid headers
                Console.WriteLine($"Warning: Failed to copy header {header.Key}: {ex.Message}");
            }
        }

        // Copy content headers
        if (remoteResponse.Content?.Headers != null)
        {
            foreach (var header in remoteResponse.Content.Headers)
            {
                if (IsHopByHopHeader(header.Key))
                    continue;

                try
                {
                    clientResponse.Headers.Add(header.Key, string.Join(", ", header.Value));
                }
                catch (Exception ex)
                {
                    // Log but don't fail for invalid headers
                    Console.WriteLine($"Warning: Failed to copy content header {header.Key}: {ex.Message}");
                }
            }
        }
    }

    private async Task WriteResponseToClient(HttpListenerResponse clientResponse, byte[] responseBody, 
        string requestId, CancellationToken cancellationToken)
    {
        if (responseBody == null || responseBody.Length == 0)
            return;

        const int chunkSize = 8192;
        int offset = 0;
        
        while (offset < responseBody.Length)
        {
            if (!clientResponse.OutputStream.CanWrite)
            {
                _logger.LogError($"[{requestId}] Client stream closed during response writing");
                return;
            }

            var remainingBytes = responseBody.Length - offset;
            var bytesToWrite = Math.Min(chunkSize, remainingBytes);
            
            await clientResponse.OutputStream.WriteAsync(responseBody, offset, bytesToWrite, cancellationToken);
            await clientResponse.OutputStream.FlushAsync(cancellationToken);
            offset += bytesToWrite;
        }
    }

    private async Task<HttpResponseMessage?> ForwardRequestWithRetryAsync(HttpListenerRequest request, 
        byte[] requestBody, string requestId, CancellationToken cancellationToken)
    {
        HttpResponseMessage? remoteResponse = null;
        var maxRetries = 2;
        var retryDelay = TimeSpan.FromSeconds(1);

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                remoteResponse = await ForwardRequestAsync(request, requestBody, cancellationToken);
                break; // Success, exit retry loop
            }
            catch (HttpRequestException ex) when (attempt < maxRetries && IsRetryableError(ex) && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError($"[{requestId}] Attempt {attempt + 1} failed, retrying: {ex.Message}");
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay = TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * 2); // Exponential backoff
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException && attempt < maxRetries && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError($"[{requestId}] Attempt {attempt + 1} timed out, retrying");
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay = TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * 2);
            }
        }

        return remoteResponse;
    }

    private static bool IsClientConnected(HttpListenerResponse response)
    {
        try
        {
            return response?.OutputStream?.CanWrite == true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRetryableError(HttpRequestException ex)
    {
        var message = ex.Message.ToLowerInvariant();
        return message.Contains("network") || 
               message.Contains("connection") || 
               message.Contains("timeout") ||
               message.Contains("reset");
    }

    private async Task<byte[]> ReadRequestBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasEntityBody)
            return Array.Empty<byte>();

        using var bodyStream = request.InputStream;
        using var memoryStream = new MemoryStream();
        
        var buffer = new byte[_config.StreamBufferSize];
        int bytesRead;
        
        while ((bytesRead = await bodyStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await memoryStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
        }
        
        return memoryStream.ToArray();
    }

    private async Task<HttpResponseMessage> ForwardRequestAsync(HttpListenerRequest request, byte[] requestBody, CancellationToken cancellationToken)
    {
        var targetUri = new Uri($"{_config.RemoteBaseUrl}{request.Url!.PathAndQuery}");
        
        using var requestMessage = new HttpRequestMessage(new HttpMethod(request.HttpMethod), targetUri);
        requestMessage.Headers.ConnectionClose = false;

        // Copy headers
        foreach (string headerName in request.Headers.AllKeys)
        {
            if (IsHopByHopHeader(headerName))
                continue;

            var headerValue = request.Headers[headerName];
            if (string.IsNullOrEmpty(headerValue))
                continue;

            try
            {
                if (headerName.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    requestMessage.Headers.Host = $"{_config.RemoteAddress}:{_config.RemotePort}";
                }
                else if (headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                {
                    requestMessage.Headers.Add("Connection", "keep-alive");
                }
                else if (!requestMessage.Headers.TryAddWithoutValidation(headerName, headerValue))
                {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(headerName, headerValue);
                }
            }
            catch (Exception ex)
            {
                // Log but continue for invalid headers
                Console.WriteLine($"Warning: Failed to copy request header {headerName}: {ex.Message}");
            }
        }

        if (!requestMessage.Headers.Contains("Connection"))
        {
            requestMessage.Headers.Add("Connection", "keep-alive");
        }

        // Add request body if present
        if (requestBody.Length > 0)
        {
            requestMessage.Content = new ByteArrayContent(requestBody);
            if (request.ContentType != null)
            {
                requestMessage.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
            }
        }

        return await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task SendTimeoutResponse(HttpListenerResponse response)
    {
        try
        {
            if (response?.OutputStream?.CanWrite == true)
            {
                response.StatusCode = 504; // Gateway Timeout
                response.ContentType = "text/plain; charset=utf-8";
                response.KeepAlive = false;
                
                var message = "Proxy Error: Request timeout"u8.ToArray();
                response.ContentLength64 = message.Length;
                
                await response.OutputStream.WriteAsync(message);
                response.Close();
            }
        }
        catch
        {
            // Ignore errors when sending timeout response
        }
    }

    private static async Task SendErrorResponse(HttpListenerResponse response, string message, int statusCode)
    {
        try
        {
            if (response?.OutputStream?.CanWrite == true)
            {
                response.StatusCode = statusCode;
                response.ContentType = "text/plain; charset=utf-8";
                response.KeepAlive = false;
                
                var buffer = System.Text.Encoding.UTF8.GetBytes($"Proxy Error: {message}");
                response.ContentLength64 = buffer.Length;
                
                await response.OutputStream.WriteAsync(buffer);
                response.Close();
            }
        }
        catch
        {
            // Ignore errors when sending error response
        }
    }

    private static bool IsHopByHopHeader(string headerName)
    {
        return headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Trailers", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient?.Dispose();
            _disposed = true;
        }
    }
}
