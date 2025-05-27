namespace HttpLogger.Core;

public class ProxyConfiguration
{
    public string LocalAddress { get; set; } = string.Empty;
    public int LocalPort { get; set; }
    public string RemoteAddress { get; set; } = string.Empty;
    public int RemotePort { get; set; }
    public bool IgnoreSslErrors { get; set; }
    public string OutputPath { get; set; } = string.Empty;

    public string LocalUrl => $"http://{LocalAddress}:{LocalPort}/";
    public string RemoteBaseUrl => RemotePort == 443 || RemotePort == 8443 
        ? $"https://{RemoteAddress}:{RemotePort}" 
        : $"http://{RemoteAddress}:{RemotePort}";
}