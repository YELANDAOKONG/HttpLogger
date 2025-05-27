using System.Net;
using System.Text;
using Spectre.Console;

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

        // Register cancellation to stop the listener
        cancellationToken.Register(() =>
        {
            try
            {
                _listener?.Stop();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, ignore
            }
        });

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Use a timeout to make GetContextAsync responsive to cancellation
                    var contextTask = _listener.GetContextAsync();
                    var delayTask = Task.Delay(1000, cancellationToken); // Check cancellation every second
                    
                    var completedTask = await Task.WhenAny(contextTask, delayTask);
                    
                    if (completedTask == delayTask)
                    {
                        // Timeout occurred, check if cancellation was requested
                        if (cancellationToken.IsCancellationRequested)
                            break;
                        continue; // Continue waiting for requests
                    }

                    var context = await contextTask;
                    
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
                    }, CancellationToken.None); // Don't cancel individual request handling
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995) // ERROR_OPERATION_ABORTED
                {
                    // Expected when cancellation is requested
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // Expected when cancellation is requested
                    break;
                }
                catch (InvalidOperationException)
                {
                    // Listener stopped
                    break;
                }
            }
        }
        finally
        {
            try
            {
                if (_listener.IsListening)
                {
                    _listener.Stop();
                }
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }
            
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
            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }
            
            _messageHandler?.Dispose();
            _disposed = true;
        }
    }
}