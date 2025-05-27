using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace HttpLogger.Core;

public class RequestResponseLogger : IDisposable
{
    private readonly string _outputPath;
    private readonly string _masterLogPath;
    private readonly FileStream _masterLogStream;
    private readonly StreamWriter _masterLogWriter;
    private readonly object _masterLogLock = new();
    private readonly ConcurrentDictionary<string, RequestResponsePair> _activeRequests = new();
    private bool _disposed;

    public RequestResponseLogger(string outputPath)
    {
        _outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(_outputPath);

        _masterLogPath = Path.Combine(_outputPath, $"http_log_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        
        // Open master log file with shared read access
        _masterLogStream = new FileStream(_masterLogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _masterLogWriter = new StreamWriter(_masterLogStream, Encoding.UTF8) { AutoFlush = true };

        WriteToMasterLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] HTTP Logger started. Output directory: {_outputPath}");
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

        // Save request to individual file
        var requestFilePath = Path.Combine(_outputPath, $"{requestId}_request.json");
        await SaveRequestToFileAsync(requestFilePath, requestInfo);

        // Add to active requests tracking
        var pair = new RequestResponsePair { Request = requestInfo };
        _activeRequests.TryAdd(requestId, pair);

        // Log to master file
        var logMessage = $"[{timestamp:yyyy-MM-dd HH:mm:ss}] REQUEST {requestId}: {request.HttpMethod} {request.Url}";
        WriteToMasterLog(logMessage);
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

        // Save response to individual file
        var responseFilePath = Path.Combine(_outputPath, $"{requestId}_response.json");
        await SaveResponseToFileAsync(responseFilePath, responseInfo);

        // Update active requests tracking
        if (_activeRequests.TryGetValue(requestId, out var pair))
        {
            pair.Response = responseInfo;
            pair.CompletedAt = DateTime.Now;

            // Save complete request-response pair
            var pairFilePath = Path.Combine(_outputPath, $"{requestId}_complete.json");
            await SaveRequestResponsePairAsync(pairFilePath, pair);

            _activeRequests.TryRemove(requestId, out _);
        }

        // Log to master file
        var duration = DateTime.Now - timestamp;
        var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RESPONSE {requestId}: {response.StatusCode} ({duration.TotalMilliseconds:F0}ms)";
        WriteToMasterLog(logMessage);
    }

    public void LogInfo(string message)
    {
        WriteToMasterLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] INFO: {message}");
    }

    public void LogError(string message)
    {
        WriteToMasterLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {message}");
    }

    private void WriteToMasterLog(string message)
    {
        lock (_masterLogLock)
        {
            _masterLogWriter.WriteLine(message);
            Console.WriteLine(message);
        }
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
        // Calculate duration with proper typing
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
            lock (_masterLogLock)
            {
                WriteToMasterLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] HTTP Logger stopped.");
                _masterLogWriter?.Dispose();
                _masterLogStream?.Dispose();
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