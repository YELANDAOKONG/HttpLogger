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
            // Enable connection pooling and keep-alive
            UseCookies = false,
            UseProxy = false,
            MaxConnectionsPerServer = 10,
            // Keep connections alive for better performance
            Proxy = null
        };

        if (_config.IgnoreSslErrors)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = _config.Timeout, // Use configured timeout
            DefaultRequestHeaders = 
            {
                // Enable keep-alive at HTTP level
                ConnectionClose = false
            }
        };

        // Configure keep-alive settings
        _httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
        _httpClient.DefaultRequestHeaders.Add("Keep-Alive", $"timeout={_config.TimeoutSeconds}, max=100");
    }

    public async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        
        var requestId = Guid.NewGuid().ToString("N")[..8];
        var timestamp = DateTime.Now;

        // Create cancellation token that monitors client connection
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
                    // Check if client is still connected
                    if (!context.Response.OutputStream.CanWrite || !IsClientConnected(context.Response))
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
        }, cancellationToken);

        try
        {
            // Read request body with timeout
            var requestBody = await ReadRequestBodyAsync(request, cancellationToken);
            
            // Log request
            await _logger.LogRequestAsync(requestId, timestamp, request, requestBody);

            // Check if client still connected before forwarding
            if (!IsClientConnected(context.Response))
            {
                _logger.LogError($"[{requestId}] Client disconnected before forwarding request");
                return;
            }

            // Forward request to remote server with retry logic
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

            if (remoteResponse == null)
            {
                throw new HttpRequestException("Failed to get response after all retry attempts");
            }

            // Check if client still connected before processing response
            if (!IsClientConnected(context.Response))
            {
                _logger.LogError($"[{requestId}] Client disconnected before processing response");
                return;
            }

            // Stream response back to client with chunked support
            await StreamResponseToClientAsync(requestId, timestamp, response, remoteResponse, cancellationToken);
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
        catch (Exception ex)
        {
            _logger.LogError($"[{requestId}] Error: {ex.Message}");
            await SendErrorResponse(response, "Internal proxy error", 500);
        }
    }

    private static bool IsClientConnected(HttpListenerResponse response)
    {
        try
        {
            return response.OutputStream.CanWrite;
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
        
        // Add timeout for reading request body with buffer
        var buffer = new byte[8192];
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

        // Ensure keep-alive is enabled for this request
        requestMessage.Headers.ConnectionClose = false;

        // Copy headers (excluding host and connection headers)
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
                    // Force keep-alive
                    requestMessage.Headers.Add("Connection", "keep-alive");
                }
                else if (!requestMessage.Headers.TryAddWithoutValidation(headerName, headerValue))
                {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(headerName, headerValue);
                }
            }
            catch
            {
                // Ignore invalid headers
            }
        }

        // Ensure keep-alive is set if not already present
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

        // Send with cancellation token for timeout control
        return await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task StreamResponseToClientAsync(string requestId, DateTime timestamp, HttpListenerResponse clientResponse, HttpResponseMessage remoteResponse, CancellationToken cancellationToken)
    {
        clientResponse.StatusCode = (int)remoteResponse.StatusCode;
        clientResponse.StatusDescription = remoteResponse.ReasonPhrase ?? string.Empty;

        // Enable keep-alive for client response
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
                    // Maintain keep-alive
                    clientResponse.Headers.Add("Connection", "keep-alive");
                }
                else
                {
                    clientResponse.Headers.Add(header.Key, string.Join(", ", header.Value));
                }
            }
            catch
            {
                // Ignore invalid headers
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
                catch
                {
                    // Ignore invalid headers
                }
            }
        }

        // Stream the response body
        using var responseStream = await remoteResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var memoryStream = new MemoryStream();
        
        // Buffer for chunked reading
        var buffer = new byte[8192];
        int totalBytesRead = 0;
        
        try
        {
            // First, read the entire response for logging
            int bytesRead;
            while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await memoryStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalBytesRead += bytesRead;

                // Check if client is still connected during streaming
                if (!IsClientConnected(clientResponse))
                {
                    _logger.LogError($"[{requestId}] Client disconnected during response streaming");
                    return;
                }
            }

            var responseBody = memoryStream.ToArray();
            
            // Log the complete response
            await _logger.LogResponseAsync(requestId, timestamp, remoteResponse, responseBody);

            // Check client connection one more time before writing
            if (!IsClientConnected(clientResponse))
            {
                _logger.LogError($"[{requestId}] Client disconnected before sending response");
                return;
            }

            // Write response body to client
            if (responseBody.Length > 0)
            {
                clientResponse.ContentLength64 = responseBody.Length;
                
                // Use chunked writing for large responses
                const int chunkSize = 8192;
                int offset = 0;
                
                while (offset < responseBody.Length)
                {
                    // Check client connection before each chunk
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

            clientResponse.Close();
        }
        catch (IOException ex)  // when (x.Message.Contains("network name is no longer available"))
        {
            _logger.LogError($"[{requestId}] Client connection lost during response: {ex.Message}");
            // Don't rethrow - this is expected when client disconnects
        }
    }

    private static async Task SendTimeoutResponse(HttpListenerResponse response)
    {
        try
        {
            if (response.OutputStream.CanWrite)
            {
                response.StatusCode = 504; // Gateway Timeout
                response.ContentType = "text/plain; charset=utf-8";
                response.KeepAlive = false; // Close connection on timeout
                
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
            if (response.OutputStream.CanWrite)
            {
                response.StatusCode = statusCode;
                response.ContentType = "text/plain; charset=utf-8";
                response.KeepAlive = false; // Close connection on error
                
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
