using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace HttpLogger.Core;

public class RequestResponseLogger : IDisposable
{
    private readonly string _sessionPath;
    private readonly ConcurrentDictionary<string, RequestResponsePair> _activeRequests = new();
    private readonly LoggingQueue _loggingQueue;
    private readonly RealtimeLogQueue _realtimeLogQueue;
    private bool _disposed;

    public string SessionPath => _sessionPath;

    public RequestResponseLogger(string basePath)
    {
        // Create session-specific subdirectory
        var sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _sessionPath = Path.Combine(Path.GetFullPath(basePath), $"HttpLogger_Session_{sessionId}");
        Directory.CreateDirectory(_sessionPath);

        // Create subdirectories
        Directory.CreateDirectory(Path.Combine(_sessionPath, "requests"));
        Directory.CreateDirectory(Path.Combine(_sessionPath, "responses"));
        Directory.CreateDirectory(Path.Combine(_sessionPath, "complete"));
        Directory.CreateDirectory(Path.Combine(_sessionPath, "raw"));

        // Initialize background logging queues
        _loggingQueue = new LoggingQueue(_sessionPath);
        _realtimeLogQueue = new RealtimeLogQueue(_sessionPath);

        LogInfo($"HTTP Logger session started. Session directory: {_sessionPath}");
    }

    public Task LogRequestAsync(string requestId, DateTime timestamp, HttpListenerRequest request, byte[] body)
    {
        var requestInfo = new RequestInfo
        {
            RequestId = requestId,
            Timestamp = timestamp,
            Method = request.HttpMethod,
            Url = request.Url?.ToString() ?? string.Empty,
            Headers = ExtractHeaders(request.Headers),
            Body = body,
            ContentType = request.ContentType ?? string.Empty
        };

        // Prepare data for background processing
        var requestData = new
        {
            requestInfo.RequestId,
            requestInfo.Timestamp,
            requestInfo.Method,
            requestInfo.Url,
            requestInfo.Headers,
            requestInfo.ContentType,
            BodyBase64 = requestInfo.Body.Length > 0 ? Convert.ToBase64String(requestInfo.Body) : null,
            BodyText = TryGetTextContent(requestInfo.Body, requestInfo.ContentType)
        };

        var rawRequest = GenerateRawHttpRequest(request, body);

        // Queue for background processing (non-blocking)
        _loggingQueue.EnqueueLogEntry(new LogEntry
        {
            RequestId = requestId,
            Timestamp = timestamp,
            Type = LogEntryType.Request,
            RequestData = requestData,
            RawContent = rawRequest
        });

        // Queue realtime logs for background processing too
        var summaryMessage = $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] REQUEST {requestId}: {request.HttpMethod} {request.Url}";
        var rawMessage = $"=== REQUEST {requestId} at {timestamp:yyyy-MM-dd HH:mm:ss.fff} ===\n{rawRequest}\n";
        
        _realtimeLogQueue.EnqueueRealtimeLog(new RealtimeLogEntry
        {
            SummaryMessage = summaryMessage,
            RawMessage = rawMessage
        });

        // Add to active requests tracking
        var pair = new RequestResponsePair { Request = requestInfo };
        _activeRequests.TryAdd(requestId, pair);

        // Only console output (fast operation) - no file I/O on main thread
        AnsiConsole.MarkupLine($"[dim]{timestamp:HH:mm:ss.fff}[/] [bold green]→[/] [cyan]{request.HttpMethod}[/] {request.Url?.AbsolutePath} [dim]({requestId})[/]");

        return Task.CompletedTask;
    }

    public Task LogResponseAsync(string requestId, DateTime timestamp, HttpResponseMessage response, byte[] body)
    {
        var responseInfo = new ResponseInfo
        {
            RequestId = requestId,
            Timestamp = timestamp,
            StatusCode = (int)response.StatusCode,
            StatusDescription = response.ReasonPhrase ?? string.Empty,
            Headers = ExtractHeaders(response.Headers, response.Content?.Headers),
            Body = body,
            ContentType = response.Content?.Headers?.ContentType?.ToString() ?? string.Empty
        };

        // Prepare data for background processing
        var responseData = new
        {
            responseInfo.RequestId,
            responseInfo.Timestamp,
            responseInfo.StatusCode,
            responseInfo.StatusDescription,
            responseInfo.Headers,
            responseInfo.ContentType,
            BodyBase64 = responseInfo.Body.Length > 0 ? Convert.ToBase64String(responseInfo.Body) : null,
            BodyText = TryGetTextContent(responseInfo.Body, responseInfo.ContentType)
        };

        var rawResponse = GenerateRawHttpResponse(response, body);

        // Queue for background processing (non-blocking)
        _loggingQueue.EnqueueLogEntry(new LogEntry
        {
            RequestId = requestId,
            Timestamp = timestamp,
            Type = LogEntryType.Response,
            ResponseData = responseData,
            RawContent = rawResponse
        });

        // Update active requests tracking and queue complete data
        if (_activeRequests.TryGetValue(requestId, out var pair))
        {
            pair.Response = responseInfo;
            pair.CompletedAt = DateTime.Now;

            // Queue complete pair for background processing
            double? durationMs = null;
            if (pair.CompletedAt.HasValue)
            {
                durationMs = (pair.CompletedAt.Value - pair.Request.Timestamp).TotalMilliseconds;
            }

            var completeData = new
            {
                RequestId = pair.Request.RequestId,
                StartTime = pair.Request.Timestamp,
                CompletedAt = pair.CompletedAt,
                DurationMs = durationMs,
                Request = new
                {
                    pair.Request.Method,
                    pair.Request.Url,
                    pair.Request.Headers,
                    pair.Request.ContentType,
                    BodyBase64 = pair.Request.Body.Length > 0 ? Convert.ToBase64String(pair.Request.Body) : null,
                    BodyText = TryGetTextContent(pair.Request.Body, pair.Request.ContentType)
                },
                Response = new
                {
                    pair.Response.StatusCode,
                    pair.Response.StatusDescription,
                    pair.Response.Headers,
                    pair.Response.ContentType,
                    BodyBase64 = pair.Response.Body.Length > 0 ? Convert.ToBase64String(pair.Response.Body) : null,
                    BodyText = TryGetTextContent(pair.Response.Body, pair.Response.ContentType)
                }
            };

            _loggingQueue.EnqueueLogEntry(new LogEntry
            {
                RequestId = requestId,
                Timestamp = timestamp,
                Type = LogEntryType.Complete,
                CompleteData = completeData
            });

            _activeRequests.TryRemove(requestId, out _);
        }

        // Queue realtime logs for background processing
        var duration = DateTime.Now - timestamp;
        var summaryMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] RESPONSE {requestId}: {response.StatusCode} ({duration.TotalMilliseconds:F0}ms)";
        var rawMessage = $"=== RESPONSE {requestId} at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===\n{rawResponse}\n";
        
        _realtimeLogQueue.EnqueueRealtimeLog(new RealtimeLogEntry
        {
            SummaryMessage = summaryMessage,
            RawMessage = rawMessage
        });

        // Only console output (fast operation) - no file I/O on main thread
        var statusColor = GetStatusColor((int)response.StatusCode);
        AnsiConsole.MarkupLine($"[dim]{DateTime.Now:HH:mm:ss.fff}[/] [bold red]←[/] [{statusColor}]{response.StatusCode}[/] [dim]({duration.TotalMilliseconds:F0}ms) ({requestId})[/]");

        return Task.CompletedTask;
    }

    private static string GetStatusColor(int statusCode)
    {
        return statusCode switch
        {
            >= 200 and < 300 => "green",
            >= 300 and < 400 => "yellow",
            >= 400 and < 500 => "orange3",
            >= 500 => "red",
            _ => "white"
        };
    }

    public void LogInfo(string message)
    {
        var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] INFO: {message}";
        
        // Queue for background processing
        _realtimeLogQueue.EnqueueRealtimeLog(new RealtimeLogEntry
        {
            SummaryMessage = logMessage,
            RawMessage = null
        });
        
        // Only console output on main thread
        AnsiConsole.MarkupLine($"[dim]{DateTime.Now:HH:mm:ss.fff}[/] [bold blue]ℹ[/] {message}");
    }

    public void LogError(string message)
    {
        var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ERROR: {message}";
        
        // Queue for background processing
        _realtimeLogQueue.EnqueueRealtimeLog(new RealtimeLogEntry
        {
            SummaryMessage = logMessage,
            RawMessage = null
        });
        
        // Only console output on main thread
        AnsiConsole.MarkupLine($"[dim]{DateTime.Now:HH:mm:ss.fff}[/] [bold red]✗[/] {message}");
    }

    // 保留所有现有的辅助方法
    private static string GenerateRawHttpRequest(HttpListenerRequest request, byte[] body)
    {
        var sb = new StringBuilder();
        
        // Request line
        sb.AppendLine($"{request.HttpMethod} {request.Url?.PathAndQuery} HTTP/1.1");
        
        // Headers
        foreach (string headerName in request.Headers.AllKeys)
        {
            if (headerName != null)
            {
                sb.AppendLine($"{headerName}: {request.Headers[headerName]}");
            }
        }
        
        sb.AppendLine(); // Empty line between headers and body
        
        // Body
        if (body.Length > 0)
        {
            if (TryGetTextContent(body, request.ContentType ?? string.Empty) is string textContent)
            {
                sb.AppendLine(textContent);
            }
            else
            {
                sb.AppendLine($"[Binary content: {body.Length} bytes]");
            }
        }

        return sb.ToString();
    }

    private static string GenerateRawHttpResponse(HttpResponseMessage response, byte[] body)
    {
        var sb = new StringBuilder();
        
        // Status line
        sb.AppendLine($"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase}");
        
        // Headers
        foreach (var header in response.Headers)
        {
            sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        }
        
        if (response.Content?.Headers != null)
        {
            foreach (var header in response.Content.Headers)
            {
                sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
            }
        }
        
        sb.AppendLine(); // Empty line between headers and body
        
        // Body
        if (body.Length > 0)
        {
            var contentType = response.Content?.Headers?.ContentType?.ToString() ?? string.Empty;
            if (TryGetTextContent(body, contentType) is string textContent)
            {
                sb.AppendLine(textContent);
            }
            else
            {
                sb.AppendLine($"[Binary content: {body.Length} bytes]");
            }
        }

        return sb.ToString();
    }

    private static Dictionary<string, string> ExtractHeaders(System.Collections.Specialized.NameValueCollection headers)
    {
        var result = new Dictionary<string, string>();
        foreach (string? key in headers.AllKeys)
        {
            if (key != null)
            {
                result[key] = headers[key] ?? string.Empty;
            }
        }
        return result;
    }

    private static Dictionary<string, string> ExtractHeaders(System.Net.Http.Headers.HttpHeaders headers1, System.Net.Http.Headers.HttpHeaders? headers2 = null)
    {
        var result = new Dictionary<string, string>();
        
        foreach (var header in headers1)
        {
            result[header.Key] = string.Join(", ", header.Value);
        }

        if (headers2 != null)
        {
            foreach (var header in headers2)
            {
                result[header.Key] = string.Join(", ", header.Value);
            }
        }

        return result;
    }

    private static string? TryGetTextContent(byte[] body, string contentType)
    {
        if (body.Length == 0)
            return null;

        // Only try to decode text content types
        if (string.IsNullOrEmpty(contentType) || 
            (!contentType.Contains("text/", StringComparison.OrdinalIgnoreCase) &&
             !contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase) &&
             !contentType.Contains("application/xml", StringComparison.OrdinalIgnoreCase) &&
             !contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(body);
        }
        catch
        {
            return null;
        }
    }

    public async Task FlushAsync()
    {
        await _loggingQueue.FlushAsync();
        await _realtimeLogQueue.FlushAsync();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            LogInfo("HTTP Logger session stopped.");
            
            _loggingQueue?.Dispose();
            _realtimeLogQueue?.Dispose();
            _disposed = true;
        }
    }

    private class RequestInfo
    {
        public string RequestId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Method { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public Dictionary<string, string> Headers { get; set; } = new();
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public string ContentType { get; set; } = string.Empty;
    }

    private class ResponseInfo
    {
        public string RequestId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public int StatusCode { get; set; }
        public string StatusDescription { get; set; } = string.Empty;
        public Dictionary<string, string> Headers { get; set; } = new();
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public string ContentType { get; set; } = string.Empty;
    }

    private class RequestResponsePair
    {
        public RequestInfo Request { get; set; } = null!;
        public ResponseInfo? Response { get; set; }
        public DateTime? CompletedAt { get; set; }
    }
}