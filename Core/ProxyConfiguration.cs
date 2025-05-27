namespace HttpLogger.Core;

public class ProxyConfiguration
{
    public string LocalAddress { get; set; } = string.Empty;
    public int LocalPort { get; set; }
    public string RemoteAddress { get; set; } = string.Empty;
    public int RemotePort { get; set; }
    public bool IgnoreSslErrors { get; set; }
    public string OutputPath { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 180; // Default 3 minutes
    
    // Streaming configuration
    public bool EnableStreaming { get; set; } = false;
    public int StreamBufferSize { get; set; } = 8192; // Default 8KB buffer
    public long MaxFullLogSize { get; set; } = 10 * 1024 * 1024; // 10MB - max size for full content logging

    public string LocalUrl => $"http://{LocalAddress}:{LocalPort}/";
    public string RemoteBaseUrl => RemotePort == 443 || RemotePort == 8443 
        ? $"https://{RemoteAddress}:{RemotePort}" 
        : $"http://{RemoteAddress}:{RemotePort}";
    
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
}