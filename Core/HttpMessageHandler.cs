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
            Timeout = TimeSpan.FromMinutes(5) // Reasonable timeout
        };
    }

    public async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        
        var requestId = Guid.NewGuid().ToString("N")[..8];
        var timestamp = DateTime.Now;

        try
        {
            // Read request body
            var requestBody = await ReadRequestBodyAsync(request);
            
            // Log request
            await _logger.LogRequestAsync(requestId, timestamp, request, requestBody);

            // Forward request to remote server
            var remoteResponse = await ForwardRequestAsync(request, requestBody);
            
            // Read response body
            var responseBody = await remoteResponse.Content.ReadAsByteArrayAsync();
            
            // Log response
            await _logger.LogResponseAsync(requestId, timestamp, remoteResponse, responseBody);

            // Send response back to client
            await SendResponseToClientAsync(response, remoteResponse, responseBody);
        }
        catch (Exception ex)
        {
            _logger.LogError($"[{requestId}] Error: {ex.Message}");
            throw;
        }
    }

    private async Task<byte[]> ReadRequestBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
            return Array.Empty<byte>();

        using var bodyStream = request.InputStream;
        using var memoryStream = new MemoryStream();
        await bodyStream.CopyToAsync(memoryStream);
        return memoryStream.ToArray();
    }

    private async Task<HttpResponseMessage> ForwardRequestAsync(HttpListenerRequest request, byte[] requestBody)
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

        return await _httpClient.SendAsync(requestMessage);
    }

    private async Task SendResponseToClientAsync(HttpListenerResponse response, HttpResponseMessage remoteResponse, byte[] responseBody)
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

        // Write response body
        if (responseBody.Length > 0)
        {
            response.ContentLength64 = responseBody.Length;
            await response.OutputStream.WriteAsync(responseBody);
        }

        response.Close();
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