using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace HttpLogger.Core;

public class RequestResponseLogger : IDisposable
{
    private readonly string _sessionPath;
    private readonly string _summaryLogPath;
    private readonly string _rawLogPath;
    private readonly FileStream _summaryLogStream;
    private readonly FileStream _rawLogStream;
    private readonly StreamWriter _summaryLogWriter;
    private readonly StreamWriter _rawLogWriter;
    private readonly object _logLock = new();
    private readonly ConcurrentDictionary<string, RequestResponsePair> _activeRequests = new();
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

        // Initialize log files
        _summaryLogPath = Path.Combine(_sessionPath, "summary.log");
        _rawLogPath = Path.Combine(_sessionPath, "raw_traffic.log");

        _summaryLogStream = new FileStream(_summaryLogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _rawLogStream = new FileStream(_rawLogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        
        _summaryLogWriter = new StreamWriter(_summaryLogStream, Encoding.UTF8) { AutoFlush = true };
        _rawLogWriter = new StreamWriter(_rawLogStream, Encoding.UTF8) { AutoFlush = true };

        LogInfo($"HTTP Logger session started. Session directory: {_sessionPath}");
    }

    public async Task LogRequestAsync(string requestId, DateTime timestamp, HttpListenerRequest request, byte[] body)
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

        // Generate timestamped filename
        var timestampStr = timestamp.ToString("yyyyMMdd_HHmmss_fff");
        
        // Save request to individual JSON file
        var requestJsonPath = Path.Combine(_sessionPath, "requests", $"{timestampStr}_{requestId}_request.json");
        await SaveRequestToFileAsync(requestJsonPath, requestInfo);

        // Save RAW request
        var requestRawPath = Path.Combine(_sessionPath, "raw", $"{timestampStr}_{requestId}_request.txt");
        await SaveRawRequestAsync(requestRawPath, request, body);

        // Add to active requests tracking
        var pair = new RequestResponsePair { Request = requestInfo };
        _activeRequests.TryAdd(requestId, pair);

        // Log to both summary and raw files
        var summaryMessage = $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] REQUEST {requestId}: {request.HttpMethod} {request.Url}";
        var rawRequest = GenerateRawHttpRequest(request, body);
        
        LogToFiles(summaryMessage, $"=== REQUEST {requestId} at {timestamp:yyyy-MM-dd HH:mm:ss.fff} ===\n{rawRequest}\n");

        // Console output with colors
        AnsiConsole.MarkupLine($"[dim]{timestamp:HH:mm:ss.fff}[/] [bold green]→[/] [cyan]{request.HttpMethod}[/] {request.Url?.AbsolutePath} [dim]({requestId})[/]");
    }

    public async Task LogResponseAsync(string requestId, DateTime timestamp, HttpResponseMessage response, byte[] body)
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

        // Generate timestamped filename
        var timestampStr = timestamp.ToString("yyyyMMdd_HHmmss_fff");
        
        // Save response to individual JSON file
        var responseJsonPath = Path.Combine(_sessionPath, "responses", $"{timestampStr}_{requestId}_response.json");
        await SaveResponseToFileAsync(responseJsonPath, responseInfo);

        // Save RAW response
        var responseRawPath = Path.Combine(_sessionPath, "raw", $"{timestampStr}_{requestId}_response.txt");
        await SaveRawResponseAsync(responseRawPath, response, body);

        // Update active requests tracking
        if (_activeRequests.TryGetValue(requestId, out var pair))
        {
            pair.Response = responseInfo;
            pair.CompletedAt = DateTime.Now;

            // Save complete request-response pair
            var completeJsonPath = Path.Combine(_sessionPath, "complete", $"{timestampStr}_{requestId}_complete.json");
            await SaveRequestResponsePairAsync(completeJsonPath, pair);

            _activeRequests.TryRemove(requestId, out _);
        }

        // Calculate duration
        var duration = DateTime.Now - timestamp;
        var summaryMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] RESPONSE {requestId}: {response.StatusCode} ({duration.TotalMilliseconds:F0}ms)";
        var rawResponse = GenerateRawHttpResponse(response, body);
        
        LogToFiles(summaryMessage, $"=== RESPONSE {requestId} at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===\n{rawResponse}\n");

        // Console output with colors
        var statusColor = GetStatusColor((int)response.StatusCode);
        AnsiConsole.MarkupLine($"[dim]{DateTime.Now:HH:mm:ss.fff}[/] [bold red]←[/] [{statusColor}]{response.StatusCode}[/] [dim]({duration.TotalMilliseconds:F0}ms) ({requestId})[/]");
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
        LogToFiles(logMessage, null);
        AnsiConsole.MarkupLine($"[dim]{DateTime.Now:HH:mm:ss.fff}[/] [bold blue]ℹ[/] {message}");
    }

    public void LogError(string message)
    {
        var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ERROR: {message}";
        LogToFiles(logMessage, null);
        AnsiConsole.MarkupLine($"[dim]{DateTime.Now:HH:mm:ss.fff}[/] [bold red]✗[/] {message}");
    }

    private void LogToFiles(string summaryMessage, string? rawMessage)
    {
        lock (_logLock)
        {
            _summaryLogWriter.WriteLine(summaryMessage);
            
            if (rawMessage != null)
            {
                _rawLogWriter.WriteLine(rawMessage);
            }
        }
    }

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

    private static async Task SaveRawRequestAsync(string filePath, HttpListenerRequest request, byte[] body)
    {
        var rawContent = GenerateRawHttpRequest(request, body);
        await File.WriteAllTextAsync(filePath, rawContent, Encoding.UTF8);
    }

    private static async Task SaveRawResponseAsync(string filePath, HttpResponseMessage response, byte[] body)
    {
        var rawContent = GenerateRawHttpResponse(response, body);
        await File.WriteAllTextAsync(filePath, rawContent, Encoding.UTF8);
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

    private static async Task SaveRequestToFileAsync(string filePath, RequestInfo request)
    {
        var requestData = new
        {
            request.RequestId,
            request.Timestamp,
            request.Method,
            request.Url,
            request.Headers,
            request.ContentType,
            BodyBase64 = request.Body.Length > 0 ? Convert.ToBase64String(request.Body) : null,
            BodyText = TryGetTextContent(request.Body, request.ContentType)
        };

        var json = JsonSerializer.Serialize(requestData, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);
    }

    private static async Task SaveResponseToFileAsync(string filePath, ResponseInfo response)
    {
        var responseData = new
        {
            response.RequestId,
            response.Timestamp,
            response.StatusCode,
            response.StatusDescription,
            response.Headers,
            response.ContentType,
            BodyBase64 = response.Body.Length > 0 ? Convert.ToBase64String(response.Body) : null,
            BodyText = TryGetTextContent(response.Body, response.ContentType)
        };

        var json = JsonSerializer.Serialize(responseData, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);
    }

    private static async Task SaveRequestResponsePairAsync(string filePath, RequestResponsePair pair)
    {
        double? durationMs = null;
        if (pair.CompletedAt.HasValue)
        {
            durationMs = (pair.CompletedAt.Value - pair.Request.Timestamp).TotalMilliseconds;
        }

        var pairData = new
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
                BodyBase64 = pair.Response.Body.Length > 0 ? Convert.ToBase64String(pair.Response.Body) : null,
                BodyText = TryGetTextContent(pair.Response.Body, pair.Response.ContentType)
            } : null
        };

        var json = JsonSerializer.Serialize(pairData, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);
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

    public void Dispose()
    {
        if (!_disposed)
        {
            lock (_logLock)
            {
                LogInfo("HTTP Logger session stopped.");
                _summaryLogWriter?.Dispose();
                _rawLogWriter?.Dispose();
                _summaryLogStream?.Dispose();
                _rawLogStream?.Dispose();
            }
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