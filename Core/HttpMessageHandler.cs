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

        var handler = new HttpClientHandler();
        if (_config.IgnoreSslErrors)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(2) // Reduced from 5 to 2 minutes
        };
    }

    public async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        
        var requestId = Guid.NewGuid().ToString("N")[..8];
        var timestamp = DateTime.Now;

        // Create cancellation token for this request with timeout
        using var requestCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var cancellationToken = requestCts.Token;

        try
        {
            // Read request body with timeout
            var requestBody = await ReadRequestBodyAsync(request, cancellationToken);
            
            // Log request
            await _logger.LogRequestAsync(requestId, timestamp, request, requestBody);

            // Forward request to remote server with timeout
            var remoteResponse = await ForwardRequestAsync(request, requestBody, cancellationToken);
            
            // Read response body with timeout
            var responseBody = await remoteResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            
            // Log response
            await _logger.LogResponseAsync(requestId, timestamp, remoteResponse, responseBody);

            // Send response back to client
            await SendResponseToClientAsync(response, remoteResponse, responseBody, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogError($"[{requestId}] Request timeout after 2 minutes");
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

    private async Task<byte[]> ReadRequestBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasEntityBody)
            return Array.Empty<byte>();

        using var bodyStream = request.InputStream;
        using var memoryStream = new MemoryStream();
        
        // Add timeout for reading request body
        await bodyStream.CopyToAsync(memoryStream, cancellationToken);
        return memoryStream.ToArray();
    }

    private async Task<HttpResponseMessage> ForwardRequestAsync(HttpListenerRequest request, byte[] requestBody, CancellationToken cancellationToken)
    {
        var targetUri = new Uri($"{_config.RemoteBaseUrl}{request.Url!.PathAndQuery}");
        
        using var requestMessage = new HttpRequestMessage(new HttpMethod(request.HttpMethod), targetUri);

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
        return await _httpClient.SendAsync(requestMessage, cancellationToken);
    }

    private async Task SendResponseToClientAsync(HttpListenerResponse response, HttpResponseMessage remoteResponse, byte[] responseBody, CancellationToken cancellationToken)
    {
        response.StatusCode = (int)remoteResponse.StatusCode;
        response.StatusDescription = remoteResponse.ReasonPhrase ?? string.Empty;

        // Copy response headers
        foreach (var header in remoteResponse.Headers)
        {
            if (IsHopByHopHeader(header.Key))
                continue;

            try
            {
                response.Headers.Add(header.Key, string.Join(", ", header.Value));
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
                    response.Headers.Add(header.Key, string.Join(", ", header.Value));
                }
                catch
                {
                    // Ignore invalid headers
                }
            }
        }

        // Write response body with timeout
        if (responseBody.Length > 0)
        {
            response.ContentLength64 = responseBody.Length;
            
            // Use timeout for writing response
            var writeTask = response.OutputStream.WriteAsync(responseBody, 0, responseBody.Length, cancellationToken);
            await writeTask;
        }

        response.Close();
    }

    private static async Task SendTimeoutResponse(HttpListenerResponse response)
    {
        try
        {
            response.StatusCode = 504; // Gateway Timeout
            response.ContentType = "text/plain; charset=utf-8";
            
            var message = "Proxy Error: Request timeout"u8.ToArray();
            response.ContentLength64 = message.Length;
            
            await response.OutputStream.WriteAsync(message);
            response.Close();
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
            response.StatusCode = statusCode;
            response.ContentType = "text/plain; charset=utf-8";
            
            var buffer = System.Text.Encoding.UTF8.GetBytes($"Proxy Error: {message}");
            response.ContentLength64 = buffer.Length;
            
            await response.OutputStream.WriteAsync(buffer);
            response.Close();
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
