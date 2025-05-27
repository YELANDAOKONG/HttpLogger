using System.Net;

namespace HttpLogger.Core;

public class HttpMessageHandler : IDisposable
{
    private readonly ProxyConfiguration _config;
    private readonly RequestResponseLogger _logger;
    private readonly HttpClient _httpClient;
    private volatile bool _disposed;

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
        
        using var timeoutCts = new CancellationTokenSource(_config.Timeout);
        var cancellationToken = timeoutCts.Token;

        try
        {
            // Read request body with timeout
            var requestBody = await ReadRequestBodyAsync(request, cancellationToken);
            
            // Log request
            await _logger.LogRequestAsync(requestId, timestamp, request, requestBody);

            // Check client connection before proceeding
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

            // Check client connection again before processing response
            if (!IsClientConnected(context.Response))
            {
                _logger.LogError($"[{requestId}] Client disconnected before processing response");
                remoteResponse.Dispose();
                return;
            }

            // Use simple client disconnect token
            using var clientDisconnectCts = new CancellationTokenSource();
            var clientDisconnectToken = clientDisconnectCts.Token;

            // Choose streaming strategy based on configuration
            if (_config.EnableStreaming)
            {
                await HandleStreamingResponseAsync(requestId, timestamp, response, remoteResponse, 
                    cancellationToken, clientDisconnectToken);
            }
            else
            {
                await HandleBufferedResponseAsync(requestId, timestamp, response, remoteResponse, 
                    cancellationToken, clientDisconnectToken);
            }
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
        CancellationToken cancellationToken, CancellationToken clientDisconnectToken)
    {
        try
        {
            // Process content length for chunked encoding
            var contentLength = remoteResponse.Content?.Headers?.ContentLength;
            var shouldLogFullContent = !contentLength.HasValue || contentLength.Value <= _config.MaxFullLogSize;
            
            // Set response status and headers before accessing the stream
            SetupClientResponse(clientResponse, remoteResponse);
            
            // Enable chunked encoding for streaming if content length is unknown
            if (!contentLength.HasValue && _config.EnableStreaming)
            {
                clientResponse.SendChunked = true;
            }
            
            // Get response stream with proper error handling
            using var responseStream = await remoteResponse.Content.ReadAsStreamAsync(cancellationToken);
        
            if (shouldLogFullContent)
            {
                // Small response: stream while logging full content
                await StreamWithDirectForwarding(requestId, timestamp, responseStream, clientResponse, 
                    remoteResponse, cancellationToken, clientDisconnectToken);
            }
            else
            {
                // Large response: stream with metadata logging only
                await StreamWithDirectForwarding(requestId, timestamp, responseStream, clientResponse, 
                    remoteResponse, cancellationToken, clientDisconnectToken, loggingMode: "metadata");
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
    /// Handles response in buffered mode - entire response is cached before forwarding
    /// </summary>
    private async Task HandleBufferedResponseAsync(string requestId, DateTime timestamp, 
        HttpListenerResponse clientResponse, HttpResponseMessage remoteResponse, 
        CancellationToken cancellationToken, CancellationToken clientDisconnectToken)
    {
        try
        {
            using var responseStream = await remoteResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var memoryStream = new MemoryStream();
            
            var buffer = new byte[_config.StreamBufferSize];
            int bytesRead;
            
            // Buffer entire response
            while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await memoryStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);

                if (clientDisconnectToken.IsCancellationRequested)
                {
                    _logger.LogError($"[{requestId}] Client disconnected during response buffering");
                    return;
                }
            }

            var responseBody = memoryStream.ToArray();
            
            // Log the complete response
            await _logger.LogResponseAsync(requestId, timestamp, remoteResponse, responseBody);

            if (clientDisconnectToken.IsCancellationRequested)
            {
                _logger.LogError($"[{requestId}] Client disconnected before sending response");
                return;
            }

            // Setup headers after we know content length
            SetupClientResponse(clientResponse, remoteResponse);
            
            // Set exact content length for buffered response
            clientResponse.ContentLength64 = responseBody.Length;

            // Send buffered response to client with improved error handling
            await WriteBufferedResponseToClient(clientResponse, responseBody, requestId, cancellationToken);

            // Only close if client hasn't disconnected
            if (!clientDisconnectToken.IsCancellationRequested)
            {
                try
                {
                    clientResponse.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[{requestId}] Error closing client response: {ex.Message}");
                }
            }
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
    /// Simplified direct forwarding for both full logging and metadata-only cases
    /// </summary>
    private async Task StreamWithDirectForwarding(string requestId, DateTime timestamp,
        Stream responseStream, HttpListenerResponse clientResponse, 
        HttpResponseMessage remoteResponse, CancellationToken cancellationToken, 
        CancellationToken clientDisconnectToken, string loggingMode = "full")
    {
        var buffer = new byte[_config.StreamBufferSize];
        var firstChunkBuffer = loggingMode == "metadata" ? new List<byte>() : null;
        var loggingBuffer = loggingMode == "full" ? new MemoryStream() : null;
        
        int bytesRead;
        long totalBytes = 0;
        bool firstChunkCaptured = false;
        const int maxFirstChunkSize = 1024; // 1KB for metadata mode
        
        try
        {
            while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                // Check client disconnect before writing
                if (clientDisconnectToken.IsCancellationRequested)
                {
                    _logger.LogError($"[{requestId}] Client disconnected during streaming");
                    break;
                }

                // Create a new byte array for the exact data we want to write
                byte[] dataToWrite = new byte[bytesRead];
                Array.Copy(buffer, 0, dataToWrite, 0, bytesRead);

                // Write using direct method to avoid parameter issues
                bool writeSuccess = await WriteToClientDirect(clientResponse, dataToWrite, requestId);
                
                if (!writeSuccess)
                {
                    _logger.LogError($"[{requestId}] Failed to write to client, stopping stream");
                    break;
                }
                
                // Handle logging based on mode
                if (loggingMode == "full" && loggingBuffer != null)
                {
                    // Full content logging
                    await loggingBuffer.WriteAsync(dataToWrite, 0, dataToWrite.Length, cancellationToken);
                }
                else if (loggingMode == "metadata" && firstChunkBuffer != null && !firstChunkCaptured)
                {
                    // Capture first chunk only for metadata logging
                    var bytesToCapture = Math.Min(bytesRead, maxFirstChunkSize - firstChunkBuffer.Count);
                    if (bytesToCapture > 0)
                    {
                        for (int i = 0; i < bytesToCapture; i++)
                        {
                            firstChunkBuffer.Add(dataToWrite[i]);
                        }
                        
                        // Check if we've captured enough
                        if (firstChunkBuffer.Count >= maxFirstChunkSize)
                        {
                            firstChunkCaptured = true;
                        }
                    }
                }

                totalBytes += bytesRead;
            }

            // Log response based on mode
            _ = Task.Run(async () =>
            {
                try
                {
                    if (loggingMode == "full" && loggingBuffer != null)
                    {
                        var responseBody = loggingBuffer.ToArray();
                        await _logger.LogResponseAsync(requestId, timestamp, remoteResponse, responseBody);
                    }
                    else if (loggingMode == "metadata" && firstChunkBuffer != null)
                    {
                        await _logger.LogResponseMetadataAsync(requestId, timestamp, remoteResponse, 
                            firstChunkBuffer.ToArray(), totalBytes);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[{requestId}] Error logging response: {ex.Message}");
                }
            }, CancellationToken.None);

            // Close the response properly
            if (!clientDisconnectToken.IsCancellationRequested && clientResponse.OutputStream?.CanWrite == true)
            {
                try
                {
                    clientResponse.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[{requestId}] Error closing client response: {ex.Message}");
                }
            }
        }
        catch (IOException ex) when (ex.Message.Contains("network") || ex.Message.Contains("connection"))
        {
            _logger.LogError($"[{requestId}] Client connection lost during streaming: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            _logger.LogError($"[{requestId}] Stream disposed during streaming");
        }
        catch (Exception ex)
        {
            _logger.LogError($"[{requestId}] Unexpected error during streaming: {ex.Message}");
            throw;
        }
        finally
        {
            loggingBuffer?.Dispose();
        }
    }

    /// <summary>
    /// Safe direct write method that avoids parameter issues
    /// </summary>
    private async Task<bool> WriteToClientDirect(HttpListenerResponse clientResponse, byte[] data, string requestId)
    {
        try
        {
            if (clientResponse?.OutputStream == null || !clientResponse.OutputStream.CanWrite)
            {
                _logger.LogError($"[{requestId}] Client response stream is not available");
                return false;
            }

            if (data == null || data.Length == 0)
            {
                return true; // Nothing to write
            }

            // Write the entire byte array directly
            await clientResponse.OutputStream.WriteAsync(data, 0, data.Length);
            await clientResponse.OutputStream.FlushAsync();
            
            return true;
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 64) // ERROR_NETNAME_DELETED
        {
            _logger.LogError($"[{requestId}] Network name no longer available during write");
            return false;
        }
        catch (IOException ex)
        {
            _logger.LogError($"[{requestId}] I/O error writing to client: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError($"[{requestId}] Error writing data to client: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Write buffered response to client
    /// </summary>
    private async Task WriteBufferedResponseToClient(HttpListenerResponse clientResponse, byte[] responseBody, 
        string requestId, CancellationToken cancellationToken)
    {
        if (responseBody == null || responseBody.Length == 0)
            return;

        if (clientResponse?.OutputStream == null || !clientResponse.OutputStream.CanWrite)
        {
            _logger.LogError($"[{requestId}] Client response stream is not writable");
            return;
        }

        const int chunkSize = 8192;
        int offset = 0;
    
        try
        {
            while (offset < responseBody.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
            
                if (!clientResponse.OutputStream.CanWrite)
                {
                    _logger.LogError($"[{requestId}] Client stream closed during response writing");
                    return;
                }
                
                var remainingBytes = responseBody.Length - offset;
                var bytesToWrite = Math.Min(chunkSize, remainingBytes);
            
                // Create exact data array to avoid parameter validation issues
                byte[] chunk = new byte[bytesToWrite];
                Array.Copy(responseBody, offset, chunk, 0, bytesToWrite);
                
                // Write the chunk directly
                await clientResponse.OutputStream.WriteAsync(chunk, 0, chunk.Length, cancellationToken);
                await clientResponse.OutputStream.FlushAsync(cancellationToken);
            
                offset += bytesToWrite;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogError($"[{requestId}] Response writing cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError($"[{requestId}] Error writing response to client: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Setup the client response with headers and status code
    /// </summary>
    private void SetupClientResponse(HttpListenerResponse clientResponse, HttpResponseMessage remoteResponse)
    {
        if (clientResponse == null)
            throw new ArgumentNullException(nameof(clientResponse));
        if (remoteResponse == null)
            throw new ArgumentNullException(nameof(remoteResponse));

        try
        {
            // Set status code
            clientResponse.StatusCode = (int)remoteResponse.StatusCode;
            clientResponse.StatusDescription = remoteResponse.ReasonPhrase ?? string.Empty;
            
            // Preserve connection
            clientResponse.KeepAlive = true;

            // Set of headers that shouldn't be copied directly
            var excludedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Transfer-Encoding", // We handle this separately with SendChunked
                "Content-Length",    // Set by buffered mode only
                "Connection",        // We set this explicitly
                "Keep-Alive",        // We set this explicitly
                "Server",            // Let HttpListener set this
                "Date"               // Let HttpListener set this
            };

            // Copy response headers while preserving original case
            foreach (var header in remoteResponse.Headers)
            {
                if (excludedHeaders.Contains(header.Key))
                    continue;

                try
                {
                    // Special case for content-type which has a direct property
                    if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        clientResponse.ContentType = string.Join(", ", header.Value);
                    }
                    else
                    {
                        clientResponse.Headers.Add(header.Key, string.Join(", ", header.Value));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to copy header {header.Key}: {ex.Message}");
                }
            }

            // Copy content headers
            if (remoteResponse.Content?.Headers != null)
            {
                foreach (var header in remoteResponse.Content.Headers)
                {
                    if (excludedHeaders.Contains(header.Key))
                        continue;

                    try
                    {
                        // Special case for content-type which has a direct property
                        if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            clientResponse.ContentType = string.Join(", ", header.Value);
                        }
                        else
                        {
                            clientResponse.Headers.Add(header.Key, string.Join(", ", header.Value));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Failed to copy content header {header.Key}: {ex.Message}");
                    }
                }
            }

            // Ensure connection headers are set correctly
            clientResponse.Headers["Connection"] = "keep-alive";
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to setup client response: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Forward request with retry logic
    /// </summary>
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

        try
        {
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
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to read request body: {ex.Message}", ex);
        }
    }

    private async Task<HttpResponseMessage> ForwardRequestAsync(HttpListenerRequest request, byte[] requestBody, CancellationToken cancellationToken)
    {
        try
        {
            var targetUri = new Uri($"{_config.RemoteBaseUrl}{request.Url!.PathAndQuery}");
            
            using var requestMessage = new HttpRequestMessage(new HttpMethod(request.HttpMethod), targetUri);
            requestMessage.Headers.ConnectionClose = false;

            // Copy headers from the original request
            foreach (string headerName in request.Headers.AllKeys)
            {
                if (IsHopByHopHeader(headerName))
                    continue;

                var headerValue = request.Headers[headerName];
                if (string.IsNullOrEmpty(headerValue))
                    continue;

                if (headerName.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    // Explicitly set Host header to match target
                    requestMessage.Headers.Host = targetUri.Authority;
                }
                else
                {
                    try
                    {
                        requestMessage.Headers.TryAddWithoutValidation(headerName, headerValue);
                    }
                    catch
                    {
                        // Skip invalid headers
                    }
                }
            }

            // Set request body if present
            if (requestBody.Length > 0)
            {
                // Use content type from original request if available
                var contentType = request.ContentType;
                requestMessage.Content = new ByteArrayContent(requestBody);
                
                if (!string.IsNullOrEmpty(contentType))
                {
                    requestMessage.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                }
                
                // Copy content-related headers
                if (request.Headers["Content-Encoding"] != null)
                {
                    requestMessage.Content.Headers.TryAddWithoutValidation("Content-Encoding", request.Headers["Content-Encoding"]);
                }
            }

            // Send request with timeout
            return await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new HttpRequestException($"Failed to forward request: {ex.Message}", ex);
        }
    }

    private async Task SendErrorResponse(HttpListenerResponse response, string message, int statusCode)
    {
        try
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            
            var errorJson = $"{{\"error\": \"{message}\"}}";
            var errorBytes = System.Text.Encoding.UTF8.GetBytes(errorJson);
            
            response.ContentLength64 = errorBytes.Length;
            await response.OutputStream.WriteAsync(errorBytes);
            response.Close();
        }
        catch
        {
            // Best effort
        }
    }

    private async Task SendTimeoutResponse(HttpListenerResponse response)
    {
        await SendErrorResponse(response, "Request timed out", 504);
    }

    /// <summary>
    /// Check if a header is a hop-by-hop header that should not be forwarded
    /// </summary>
    private static bool IsHopByHopHeader(string headerName)
    {
        if (string.IsNullOrEmpty(headerName))
            return false;
            
        var hopByHopHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Connection",
            "Keep-Alive",
            "Proxy-Authenticate",
            "Proxy-Authorization",
            "TE",
            "Trailers",
            "Transfer-Encoding",
            "Upgrade"
        };

        return hopByHopHeaders.Contains(headerName);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _httpClient.Dispose();
            }
            
            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
