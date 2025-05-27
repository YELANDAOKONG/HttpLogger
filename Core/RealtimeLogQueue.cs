using System.Collections.Concurrent;
using System.Text;

namespace HttpLogger.Core;

public class RealtimeLogQueue : IDisposable
{
    private readonly string _sessionPath;
    private readonly ConcurrentQueue<RealtimeLogEntry> _logQueue = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _processingTask;
    private readonly SemaphoreSlim _queueSemaphore = new(0);
    private readonly FileStream _summaryLogStream;
    private readonly FileStream _rawLogStream;
    private readonly StreamWriter _summaryLogWriter;
    private readonly StreamWriter _rawLogWriter;
    private bool _disposed;

    public RealtimeLogQueue(string sessionPath)
    {
        _sessionPath = sessionPath;
        
        // Initialize log files
        var summaryLogPath = Path.Combine(_sessionPath, "summary.log");
        var rawLogPath = Path.Combine(_sessionPath, "raw_traffic.log");

        _summaryLogStream = new FileStream(summaryLogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _rawLogStream = new FileStream(rawLogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        
        // Don't use AutoFlush - we'll control flushing manually
        _summaryLogWriter = new StreamWriter(_summaryLogStream, Encoding.UTF8) { AutoFlush = false };
        _rawLogWriter = new StreamWriter(_rawLogStream, Encoding.UTF8) { AutoFlush = false };

        _processingTask = Task.Run(ProcessLogEntriesAsync);
    }

    public void EnqueueRealtimeLog(RealtimeLogEntry entry)
    {
        if (_disposed) return;
        
        _logQueue.Enqueue(entry);
        _queueSemaphore.Release();
    }

    private async Task ProcessLogEntriesAsync()
    {
        var lastFlush = DateTime.Now;
        
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                // Use shorter timeout for more responsive flushing
                await _queueSemaphore.WaitAsync(100, _cancellationTokenSource.Token);
                
                // Process entries in small batches
                var processed = 0;
                while (processed < 50 && _logQueue.TryDequeue(out var entry))
                {
                    _summaryLogWriter.WriteLine(entry.SummaryMessage);
                    
                    if (entry.RawMessage != null)
                    {
                        _rawLogWriter.WriteLine(entry.RawMessage);
                    }
                    
                    processed++;
                }

                // Flush periodically or when queue is empty
                var now = DateTime.Now;
                if (processed > 0 && (now - lastFlush).TotalMilliseconds > 1000 || _logQueue.IsEmpty)
                {
                    await _summaryLogWriter.FlushAsync();
                    await _rawLogWriter.FlushAsync();
                    lastFlush = now;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing realtime logs: {ex.Message}");
            }
        }

        // Final flush
        await FlushRemainingEntriesAsync();
    }

    private async Task FlushRemainingEntriesAsync()
    {
        while (_logQueue.TryDequeue(out var entry))
        {
            _summaryLogWriter.WriteLine(entry.SummaryMessage);
            
            if (entry.RawMessage != null)
            {
                _rawLogWriter.WriteLine(entry.RawMessage);
            }
        }

        await _summaryLogWriter.FlushAsync();
        await _rawLogWriter.FlushAsync();
    }

    public async Task FlushAsync()
    {
        // Wait for queue to be empty
        while (!_logQueue.IsEmpty)
        {
            await Task.Delay(10);
        }
        
        // Force flush
        await _summaryLogWriter.FlushAsync();
        await _rawLogWriter.FlushAsync();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cancellationTokenSource.Cancel();
            
            try
            {
                _processingTask.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException)
            {
                // Ignore timeout
            }

            _summaryLogWriter?.Dispose();
            _rawLogWriter?.Dispose();
            _summaryLogStream?.Dispose();
            _rawLogStream?.Dispose();
            _cancellationTokenSource.Dispose();
            _queueSemaphore.Dispose();
            _disposed = true;
        }
    }
}

public class RealtimeLogEntry
{
    public string SummaryMessage { get; set; } = string.Empty;
    public string? RawMessage { get; set; }
}