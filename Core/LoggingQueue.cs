using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace HttpLogger.Core;

public class LoggingQueue : IDisposable
{
    private readonly string _sessionPath;
    private readonly ConcurrentQueue<LogEntry> _logQueue = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _processingTask;
    private readonly SemaphoreSlim _queueSemaphore = new(0);
    private bool _disposed;

    public LoggingQueue(string sessionPath)
    {
        _sessionPath = sessionPath;
        _processingTask = Task.Run(ProcessLogEntriesAsync);
    }

    public void EnqueueLogEntry(LogEntry entry)
    {
        if (_disposed) return;
        
        _logQueue.Enqueue(entry);
        _queueSemaphore.Release();
    }

    private async Task ProcessLogEntriesAsync()
    {
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                await _queueSemaphore.WaitAsync(_cancellationTokenSource.Token);
                
                // Process multiple entries in batch for better performance
                var batch = new List<LogEntry>();
                var maxBatchSize = 10;
                
                while (batch.Count < maxBatchSize && _logQueue.TryDequeue(out var entry))
                {
                    batch.Add(entry);
                }

                if (batch.Count > 0)
                {
                    await ProcessBatchAsync(batch);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Log error but continue processing
                Console.WriteLine($"Error processing log entries: {ex.Message}");
            }
        }

        // Process remaining entries
        await FlushRemainingEntriesAsync();
    }

    private async Task ProcessBatchAsync(List<LogEntry> batch)
    {
        var tasks = new List<Task>();
        
        foreach (var entry in batch)
        {
            tasks.Add(ProcessSingleEntryAsync(entry));
        }

        await Task.WhenAll(tasks);
    }

    private async Task ProcessSingleEntryAsync(LogEntry entry)
    {
        try
        {
            switch (entry.Type)
            {
                case LogEntryType.Request:
                    await SaveRequestAsync(entry);
                    break;
                case LogEntryType.Response:
                    await SaveResponseAsync(entry);
                    break;
                case LogEntryType.Complete:
                    await SaveCompleteAsync(entry);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving log entry {entry.RequestId}: {ex.Message}");
        }
    }

    private async Task SaveRequestAsync(LogEntry entry)
    {
        var timestampStr = entry.Timestamp.ToString("yyyyMMdd_HHmmss_fff");
        
        // JSON file
        var jsonPath = Path.Combine(_sessionPath, "requests", $"{timestampStr}_{entry.RequestId}_request.json");
        var json = JsonSerializer.Serialize(entry.RequestData, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(jsonPath, json, Encoding.UTF8);
        
        // RAW file
        var rawPath = Path.Combine(_sessionPath, "raw", $"{timestampStr}_{entry.RequestId}_request.txt");
        await File.WriteAllTextAsync(rawPath, entry.RawContent!, Encoding.UTF8);
    }

    private async Task SaveResponseAsync(LogEntry entry)
    {
        var timestampStr = entry.Timestamp.ToString("yyyyMMdd_HHmmss_fff");
        
        // JSON file
        var jsonPath = Path.Combine(_sessionPath, "responses", $"{timestampStr}_{entry.RequestId}_response.json");
        var json = JsonSerializer.Serialize(entry.ResponseData, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(jsonPath, json, Encoding.UTF8);
        
        // RAW file
        var rawPath = Path.Combine(_sessionPath, "raw", $"{timestampStr}_{entry.RequestId}_response.txt");
        await File.WriteAllTextAsync(rawPath, entry.RawContent!, Encoding.UTF8);
    }

    private async Task SaveCompleteAsync(LogEntry entry)
    {
        var timestampStr = entry.Timestamp.ToString("yyyyMMdd_HHmmss_fff");
        var completePath = Path.Combine(_sessionPath, "complete", $"{timestampStr}_{entry.RequestId}_complete.json");
        var json = JsonSerializer.Serialize(entry.CompleteData, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(completePath, json, Encoding.UTF8);
    }

    private async Task FlushRemainingEntriesAsync()
    {
        var remainingEntries = new List<LogEntry>();
        while (_logQueue.TryDequeue(out var entry))
        {
            remainingEntries.Add(entry);
        }

        if (remainingEntries.Count > 0)
        {
            await ProcessBatchAsync(remainingEntries);
        }
    }

    public async Task FlushAsync()
    {
        // Wait for queue to be empty
        while (!_logQueue.IsEmpty)
        {
            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cancellationTokenSource.Cancel();
            
            try
            {
                _processingTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Ignore timeout
            }

            _cancellationTokenSource.Dispose();
            _queueSemaphore.Dispose();
            _disposed = true;
        }
    }
}

public class LogEntry
{
    public string RequestId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public LogEntryType Type { get; set; }
    public object? RequestData { get; set; }
    public object? ResponseData { get; set; }
    public object? CompleteData { get; set; }
    public string? RawContent { get; set; }
}

public enum LogEntryType
{
    Request,
    Response,
    Complete
}