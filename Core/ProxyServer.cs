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
    private readonly SemaphoreSlim _requestSemaphore;
    private volatile bool _disposed;

    public ProxyServer(ProxyConfiguration config, RequestResponseLogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        _listener = new HttpListener();
        _listener.Prefixes.Add(_config.LocalUrl);
        
        // Configure listener for better connection handling
        _listener.TimeoutManager.IdleConnection = TimeSpan.FromMinutes(5);
        _listener.TimeoutManager.HeaderWait = TimeSpan.FromSeconds(30);
        _listener.TimeoutManager.EntityBody = TimeSpan.FromMinutes(2);
        
        _messageHandler = new HttpMessageHandler(_config, _logger);
        
        // Limit concurrent requests to prevent resource exhaustion
        _requestSemaphore = new SemaphoreSlim(50, 50);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        
        _listener.Start();
        _logger.LogInfo($"Proxy server started on {_config.LocalUrl}");

        try
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                try
                {
                    // Use GetContextAsync with cancellation
                    var context = await GetContextWithCancellationAsync(cancellationToken);
                    if (context == null)
                        continue;
                    
                    // Handle request with concurrency control in background
                    _ = Task.Run(async () => await HandleRequestWithSemaphoreAsync(context, cancellationToken), 
                                 CancellationToken.None); // Use None to prevent immediate cancellation
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
                catch (OperationCanceledException)
                {
                    // Cancellation requested
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Unexpected error in server loop: {ex.Message}");
                    // Continue running despite errors
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
            catch (Exception ex)
            {
                _logger.LogError($"Error stopping listener: {ex.Message}");
            }
            
            _logger.LogInfo("Proxy server stopped");
        }
    }

    private async Task<HttpListenerContext?> GetContextWithCancellationAsync(CancellationToken cancellationToken)
    {
        try
        {
            var contextTask = _listener.GetContextAsync();
            
            // Wait for either context or cancellation
            var tcs = new TaskCompletionSource<HttpListenerContext?>();
            
            using var registration = cancellationToken.Register(() => tcs.TrySetResult(null));
            
            var completedTask = await Task.WhenAny(contextTask, tcs.Task);
            
            if (completedTask == tcs.Task)
            {
                return await tcs.Task; // Will be null if cancelled
            }
            
            return await contextTask;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task HandleRequestWithSemaphoreAsync(HttpListenerContext context, CancellationToken serverCancellationToken)
    {
        if (_disposed) return;
        
        // Wait for semaphore with timeout to prevent hanging
        var acquired = false;
        try
        {
            acquired = await _requestSemaphore.WaitAsync(TimeSpan.FromSeconds(30), serverCancellationToken);
            if (!acquired)
            {
                _logger.LogError("Request rejected: Server too busy");
                await SendErrorResponse(context.Response, "Server too busy");
                return;
            }

            // Create timeout for individual request processing
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
            requestCts.CancelAfter(TimeSpan.FromMinutes(4)); // Total request timeout increased

            await _messageHandler.HandleRequestAsync(context);
        }
        catch (OperationCanceledException) when (serverCancellationToken.IsCancellationRequested)
        {
            // Server shutdown, ignore
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error handling request: {ex.Message}");
            try
            {
                await SendErrorResponse(context.Response, ex.Message);
            }
            catch
            {
                // Ignore errors when sending error response
            }
        }
        finally
        {
            if (acquired)
            {
                _requestSemaphore.Release();
            }
        }
    }

    private static async Task SendErrorResponse(HttpListenerResponse response, string message)
    {
        try
        {
            if (response?.OutputStream?.CanWrite == true)
            {
                response.StatusCode = 500;
                response.ContentType = "text/plain; charset=utf-8";
                response.KeepAlive = false; // Close connection on error
                
                var buffer = Encoding.UTF8.GetBytes($"Proxy Error: {message}");
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

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            
            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disposing listener: {ex.Message}");
            }
            
            _messageHandler?.Dispose();
            _requestSemaphore?.Dispose();
        }
    }
}
