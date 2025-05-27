using System.Net;
using System.Text;

namespace HttpLogger.Core;

public class ProxyServer : IDisposable
{
    private readonly ProxyConfiguration _config;
    private readonly RequestResponseLogger _logger;
    private readonly HttpListener _listener;
    private readonly HttpMessageHandler _messageHandler;
    private bool _disposed;

    public ProxyServer(ProxyConfiguration config, RequestResponseLogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        _listener = new HttpListener();
        _listener.Prefixes.Add(_config.LocalUrl);
        
        _messageHandler = new HttpMessageHandler(_config, _logger);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _listener.Start();
        _logger.LogInfo($"Proxy server started on {_config.LocalUrl}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync();
                
                // Handle request asynchronously without blocking
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _messageHandler.HandleRequestAsync(context);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error handling request: {ex.Message}");
                        await SendErrorResponse(context.Response, ex.Message);
                    }
                }, cancellationToken);
            }
        }
        catch (ObjectDisposedException)
        {
            // Expected when cancellation is requested
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 995) // ERROR_OPERATION_ABORTED
        {
            // Expected when cancellation is requested
        }
        finally
        {
            _listener.Stop();
            _logger.LogInfo("Proxy server stopped");
        }
    }

    private static async Task SendErrorResponse(HttpListenerResponse response, string message)
    {
        try
        {
            response.StatusCode = 500;
            response.ContentType = "text/plain";
            
            var buffer = Encoding.UTF8.GetBytes($"Proxy Error: {message}");
            response.ContentLength64 = buffer.Length;
            
            await response.OutputStream.WriteAsync(buffer);
            response.Close();
        }
        catch
        {
            // Ignore errors when sending error response
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _listener?.Close();
            _messageHandler?.Dispose();
            _disposed = true;
        }
    }
}