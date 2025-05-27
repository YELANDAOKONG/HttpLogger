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
    private volatile bool _disposed;

    public RealtimeLogQueue(string sessionPath)
    {
        _sessionPath = sessionPath;
        
        // Initialize log files
        var summaryLogPath = Path.Combine(_sessionPath, "summary.log");
        var rawLogPath = Path.Combine(_sessionPath, "raw_traffic.log");

        _summaryLogStream = new FileStream(summaryLogPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 4096);
        _rawLogStream = new FileStream(rawLogPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 4096);
        
        // Don't use AutoFlush - we'll control flushing manually for better performance
        _summaryLogWriter = new StreamWriter(_summaryLogStream, Encoding.UTF8, bufferSize: 4096) { AutoFlush = false };
        _rawLogWriter = new StreamWriter(_rawLogStream, Encoding.UTF8, bufferSize: 4096) { AutoFlush = false };

        _processingTask = Task.Run(ProcessLogEntriesAsync, CancellationToken.None);
    }

    public void EnqueueRealtimeLog(RealtimeLogEntry entry)
    {
        if (_disposed) return;
        
        _logQueue.Enqueue(entry);
        try
        {
            _queueSemaphore.Release();
        }
        catch (ObjectDisposedException)
        {
            // Queue disposed, ignore
        }
    }

    private async Task ProcessLogEntriesAsync()
    {
        var lastFlush = DateTime.Now;
        
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                // Use shorter timeout for more responsive flushing
                await _queueSemaphore.WaitAsync(500, _cancellationTokenSource.Token);
                
                // Process entries in larger batches for better performance
                var processed = 0;
                var batch = new List<RealtimeLogEntry>();
                
                // Collect batch
                while (processed < 100 && _logQueue.TryDequeue(out var entry))
                {
                    batch.Add(entry);
                    processed++;
                }

                // Process batch
                if (batch.Count > 0)
                {
                    await ProcessBatchAsync(batch);
                }

                // Flush periodically or when queue is empty
                var now = DateTime.Now;
                if (processed > 0 && ((now - lastFlush).TotalMilliseconds > 2000 || _logQueue.IsEmpty))
                {
                    try
                    {
                        await _summaryLogWriter.FlushAsync();
                        await _rawLogWriter.FlushAsync();
                        lastFlush = now;
                    }
                    catch (ObjectDisposedException)
                    {
                        // Writers disposed, exit
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
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

    private async Task ProcessBatchAsync(List<RealtimeLogEntry> batch)
    {
        try
        {
            foreach (var entry in batch)
            {
                if (_disposed) break;
                
                await _summaryLogWriter.WriteLineAsync(entry.SummaryMessage);
                
                if (entry.RawMessage != null)
                {
                    await _rawLogWriter.WriteLineAsync(entry.RawMessage);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Writers disposed, ignore
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing log batch: {ex.Message}");
        }
    }

    private async Task FlushRemainingEntriesAsync()
    {
        var remainingBatch = new List<RealtimeLogEntry>();
        while (_logQueue.TryDequeue(out var entry))
        {
            remainingBatch.Add(entry);
        }

        if (remainingBatch.Count > 0)
        {
            await ProcessBatchAsync(remainingBatch);
        }

        try
        {
            await _summaryLogWriter.FlushAsync();
            await _rawLogWriter.FlushAsync();
        }
        catch (ObjectDisposedException)
        {
            // Writers disposed, ignore
        }
    }

    public async Task FlushAsync()
    {
        if (_disposed) return;
        
        // Wait for queue to be empty with timeout
        var timeout = DateTime.Now.AddSeconds(10);
        while (!_logQueue.IsEmpty && DateTime.Now < timeout && !_disposed)
        {
            await Task.Delay(50);
        }
        
        // Force flush
        try
        {
            await _summaryLogWriter.FlushAsync();
            await _rawLogWriter.FlushAsync();
        }
        catch (ObjectDisposedException)
        {
            // Writers disposed, ignore
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cancellationTokenSource.Cancel();
            
            try
            {
                _processingTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Ignore timeout
            }

            try
            {
                _summaryLogWriter?.Dispose();
                _rawLogWriter?.Dispose();
                _summaryLogStream?.Dispose();
                _rawLogStream?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disposing realtime log queue: {ex.Message}");
            }
            
            _cancellationTokenSource.Dispose();
            _queueSemaphore.Dispose();
        }
    }
}

public class RealtimeLogEntry
{
    public string SummaryMessage { get; set; } = string.Empty;
    public string? RawMessage { get; set; }
}
