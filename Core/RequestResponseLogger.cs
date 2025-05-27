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
    private volatile bool _disposed;

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
        if (_disposed) return Task.CompletedTask;
        
        try
        {
            if (string.IsNullOrEmpty(requestId))
            {
                LogError("Request ID cannot be null or empty");
                return Task.CompletedTask;
            }

            if (request?.Url == null)
            {
                LogError("Request or URL cannot be null");
                return Task.CompletedTask;
            }

            body ??= Array.Empty<byte>();

            var requestInfo = new RequestInfo
            {
                RequestId = requestId,
                Timestamp = timestamp,
                Method = request.HttpMethod ?? "UNKNOWN",
                Url = request.Url.ToString(),
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

            // Console output with proper escaping for Spectre.Console
            var urlPath = request.Url.AbsolutePath;
            var escapedUrlPath = EscapeMarkup(urlPath);
            var escapedMethod = EscapeMarkup(request.HttpMethod ?? "UNKNOWN");
            var escapedRequestId = EscapeMarkup(requestId);
            AnsiConsole.MarkupLine($"[dim]{timestamp:HH:mm:ss.fff}[/] [bold green]→[/] [cyan]{escapedMethod}[/] {escapedUrlPath} [dim]({escapedRequestId})[/]");
        }
        catch (Exception ex)
        {
            LogError($"Error logging request {requestId}: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public Task LogResponseAsync(string requestId, DateTime timestamp, HttpResponseMessage response, byte[] body)
    {
        if (_disposed) return Task.CompletedTask;
        
        try
        {
            if (string.IsNullOrEmpty(requestId))
            {
                LogError("Request ID cannot be null or empty");
                return Task.CompletedTask;
            }

            if (response == null)
            {
                LogError("Response cannot be null");
                return Task.CompletedTask;
            }

            body ??= Array.Empty<byte>();

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
            if (_activeRequests.TryGetValue(requestId, out var pair) && pair?.Request != null)
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

            // Calculate duration for display
            var duration = DateTime.Now - timestamp;
            
            // Queue realtime logs for background processing
            var summaryMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] RESPONSE {requestId}: {response.StatusCode} ({duration.TotalMilliseconds:F0}ms)";
            var rawMessage = $"=== RESPONSE {requestId} at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===\n{rawResponse}\n";
            
            _realtimeLogQueue.EnqueueRealtimeLog(new RealtimeLogEntry
            {
                SummaryMessage = summaryMessage,
                RawMessage = rawMessage
            });

            // Console output with proper status color and escaping
            var statusColor = GetStatusColor((int)response.StatusCode);
            var escapedStatusCode = EscapeMarkup(response.StatusCode.ToString());
            var escapedRequestId = EscapeMarkup(requestId);
            AnsiConsole.MarkupLine($"[dim]{DateTime.Now:HH:mm:ss.fff}[/] [bold red]←[/] [{statusColor}]{escapedStatusCode}[/] [dim]({duration.TotalMilliseconds:F0}ms) ({escapedRequestId})[/]");
        }
        catch (Exception ex)
        {
            LogError($"Error logging response {requestId}: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Logs response metadata for large streaming responses
    /// </summary>
    public Task LogResponseMetadataAsync(string requestId, DateTime timestamp, HttpResponseMessage response, byte[] firstChunk, long totalBytes)
    {
        if (_disposed) return Task.CompletedTask;
        
        try
        {
            if (string.IsNullOrEmpty(requestId))
            {
                LogError("Request ID cannot be null or empty");
                return Task.CompletedTask;
            }
            if (response == null)
            {
                LogError("Response cannot be null");
                return Task.CompletedTask;
            }
            firstChunk ??= Array.Empty<byte>();
            var responseInfo = new ResponseInfo
            {
                RequestId = requestId,
                Timestamp = timestamp,
                StatusCode = (int)response.StatusCode,
                StatusDescription = response.ReasonPhrase ?? string.Empty,
                Headers = ExtractHeaders(response.Headers, response.Content?.Headers),
                Body = firstChunk, // Only first chunk for metadata logging
                ContentType = response.Content?.Headers?.ContentType?.ToString() ?? string.Empty
            };
            // Prepare metadata for logging
            var metadataResponse = new
            {
                responseInfo.RequestId,
                responseInfo.Timestamp,
                responseInfo.StatusCode,
                responseInfo.StatusDescription,
                responseInfo.Headers,
                responseInfo.ContentType,
                TotalBytes = totalBytes,
                FirstChunkBase64 = firstChunk.Length > 0 ? Convert.ToBase64String(firstChunk) : null,
                FirstChunkText = TryGetTextContent(firstChunk, responseInfo.ContentType),
                Note = $"Streaming response - {totalBytes} bytes total, first {firstChunk.Length} bytes captured"
            };
            var rawResponse = $"HTTP/{response.Version} {(int)response.StatusCode} {response.ReasonPhrase}\r\n" +
                             string.Join("\r\n", response.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}")) +
                             (response.Content?.Headers != null ? "\r\n" + string.Join("\r\n", response.Content.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}")) : "") +
                             $"\r\n\r\n[STREAMING RESPONSE - {totalBytes} bytes total, first {firstChunk.Length} bytes shown]\r\n" +
                             (firstChunk.Length > 0 ? Encoding.UTF8.GetString(firstChunk) : "[No content captured]");
            // Queue for background processing
            _loggingQueue.EnqueueLogEntry(new LogEntry
            {
                RequestId = requestId,
                Timestamp = timestamp,
                Type = LogEntryType.Response,
                ResponseData = metadataResponse,
                RawContent = rawResponse
            });
            // Console output
            var duration = DateTime.Now - timestamp;
            var statusColor = GetStatusColor((int)response.StatusCode);
            var escapedStatusCode = EscapeMarkup(response.StatusCode.ToString());
            var escapedRequestId = EscapeMarkup(requestId);
            AnsiConsole.MarkupLine($"[dim]{DateTime.Now:HH:mm:ss.fff}[/] [bold red]←[/] [{statusColor}]{escapedStatusCode}[/] [dim]({duration.TotalMilliseconds:F0}ms) ({escapedRequestId}) [STREAMED {totalBytes} bytes][/]");
        }
        catch (Exception ex)
        {
            LogError($"Error logging response metadata {requestId}: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    private object CreateCompleteDataWithMetadata(RequestResponsePair pair, long totalBytes)
    {
        if (pair?.Request == null)
        {
            LogError("Request pair or request cannot be null");
            return new { Error = "Invalid request pair" };
        }

        double? durationMs = null;
        if (pair.CompletedAt.HasValue)
        {
            durationMs = (pair.CompletedAt.Value - pair.Request.Timestamp).TotalMilliseconds;
        }

        return new
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
            Response = pair.Response != null ? new
            {
                pair.Response.StatusCode,
                pair.Response.StatusDescription,
                pair.Response.Headers,
                pair.Response.ContentType,
                FirstChunkBase64 = pair.Response.Body.Length > 0 ? Convert.ToBase64String(pair.Response.Body) : null,
                FirstChunkText = TryGetTextContent(pair.Response.Body, pair.Response.ContentType),
                TotalBytes = totalBytes,
                IsStreamingMode = true,
                Note = $"Streaming mode: Only first {pair.Response.Body.Length} bytes captured"
            } : null
        };
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
        if (_disposed) return;
        
        try
        {
            var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] INFO: {message}";
            
            // Queue for background processing
            _realtimeLogQueue.EnqueueRealtimeLog(new RealtimeLogEntry
            {
                SummaryMessage = logMessage,
                RawMessage = null
            });
            
            // Console output with proper escaping
            var escapedMessage = EscapeMarkup(message);
            AnsiConsole.MarkupLine($"[dim]{DateTime.Now:HH:mm:ss.fff}[/] [bold blue]ℹ[/] {escapedMessage}");
        }
        catch (Exception ex)
        {
            // Fallback to console directly if logging fails
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] INFO: {message} (Logging error: {ex.Message})");
        }
    }

    public void LogError(string message)
    {
        try
        {
            var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ERROR: {message}";
            
            // Queue for background processing only if not disposed
            if (!_disposed)
            {
                _realtimeLogQueue.EnqueueRealtimeLog(new RealtimeLogEntry
                {
                    SummaryMessage = logMessage,
                    RawMessage = null
                });
            }
            
            // Console output with proper escaping
            var escapedMessage = EscapeMarkup(message);
            AnsiConsole.MarkupLine($"[dim]{DateTime.Now:HH:mm:ss.fff}[/] [bold red]✗[/] {escapedMessage}");
        }
        catch (Exception ex)
        {
            // Fallback to console directly if logging fails
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ERROR: {message} (Logging error: {ex.Message})");
        }
    }

    /// <summary>
    /// Escapes markup characters for safe display in Spectre.Console
    /// </summary>
    private static string EscapeMarkup(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        return text.Replace("[", "[[").Replace("]", "]]");
    }
    
    private static string GenerateRawHttpRequest(HttpListenerRequest request, byte[] body)
    {
        var sb = new StringBuilder();
        
        try
        {
            // Request line
            sb.AppendLine($"{request.HttpMethod ?? "UNKNOWN"} {request.Url?.PathAndQuery ?? "/"} HTTP/1.1");
            
            // Headers
            if (request.Headers != null)
            {
                foreach (string? headerName in request.Headers.AllKeys)
                {
                    if (!string.IsNullOrEmpty(headerName))
                    {
                        var headerValue = request.Headers[headerName];
                        sb.AppendLine($"{headerName}: {headerValue ?? string.Empty}");
                    }
                }
            }
            
            sb.AppendLine(); // Empty line between headers and body
            
            // Body
            if (body?.Length > 0)
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
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[Error generating raw request: {ex.Message}]");
        }

        return sb.ToString();
    }

    private static string GenerateRawHttpResponse(HttpResponseMessage response, byte[] body)
    {
        var sb = new StringBuilder();
        
        try
        {
            // Status line
            sb.AppendLine($"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase ?? string.Empty}");
            
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
            if (body?.Length > 0)
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
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[Error generating raw response: {ex.Message}]");
        }

        return sb.ToString();
    }

    private static string GenerateRawHttpResponseMetadata(HttpResponseMessage response, byte[] firstChunk, long totalBytes)
    {
        var sb = new StringBuilder();
        
        try
        {
            // Status line
            sb.AppendLine($"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase ?? string.Empty}");
            
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
            
            // Body metadata
            sb.AppendLine($"[STREAMING MODE - Total: {totalBytes} bytes]");
            if (firstChunk?.Length > 0)
            {
                var contentType = response.Content?.Headers?.ContentType?.ToString() ?? string.Empty;
                if (TryGetTextContent(firstChunk, contentType) is string textContent)
                {
                    sb.AppendLine($"[First {firstChunk.Length} bytes]:");
                    sb.AppendLine(textContent);
                    sb.AppendLine("[... content truncated in streaming mode ...]");
                }
                else
                {
                    sb.AppendLine($"[Binary content - first {firstChunk.Length} bytes of {totalBytes} total]");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[Error generating raw response metadata: {ex.Message}]");
        }

        return sb.ToString();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0B";
        
        string[] suffixes = { "B", "KB", "MB", "GB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1}{suffixes[counter]}";
    }

    private static Dictionary<string, string> ExtractHeaders(System.Collections.Specialized.NameValueCollection? headers)
    {
        var result = new Dictionary<string, string>();
        
        if (headers == null) return result;
        
        try
        {
            foreach (string? key in headers.AllKeys)
            {
                if (!string.IsNullOrEmpty(key))
                {
                    result[key] = headers[key] ?? string.Empty;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Error extracting headers: {ex.Message}");
        }
        
        return result;
    }

    private static Dictionary<string, string> ExtractHeaders(System.Net.Http.Headers.HttpHeaders? headers1, System.Net.Http.Headers.HttpHeaders? headers2 = null)
    {
        var result = new Dictionary<string, string>();
        
        try
        {
            if (headers1 != null)
            {
                foreach (var header in headers1)
                {
                    result[header.Key] = string.Join(", ", header.Value);
                }
            }

            if (headers2 != null)
            {
                foreach (var header in headers2)
                {
                    result[header.Key] = string.Join(", ", header.Value);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Error extracting HTTP headers: {ex.Message}");
        }

        return result;
    }

    private static string? TryGetTextContent(byte[] body, string contentType)
    {
        if (body?.Length == 0 || body == null)
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
        if (_disposed) return;
        
        try
        {
            await _loggingQueue.FlushAsync();
            await _realtimeLogQueue.FlushAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error flushing logs: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            
            LogInfo("HTTP Logger session stopped.");
            
            try
            {
                _loggingQueue?.Dispose();
                _realtimeLogQueue?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disposing logger: {ex.Message}");
            }
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
