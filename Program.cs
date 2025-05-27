using HttpLogger.Core;

namespace HttpLogger;

static class Program
{
    public static async Task Main(string[] args)
    {
        try
        {
            var config = ParseArguments(args);
            if (config == null)
            {
                ShowUsage();
                return;
            }

            var logger = new RequestResponseLogger(config.OutputPath);
            var proxyServer = new ProxyServer(config, logger);

            Console.WriteLine($"Starting HTTP Logger Proxy...");
            Console.WriteLine($"Local: {config.LocalAddress}:{config.LocalPort}");
            Console.WriteLine($"Remote: {config.RemoteAddress}:{config.RemotePort}");
            Console.WriteLine($"Output: {config.OutputPath}");
            Console.WriteLine($"Ignore SSL Errors: {config.IgnoreSslErrors}");
            Console.WriteLine("Press Ctrl+C to stop...");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            await proxyServer.StartAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static ProxyConfiguration? ParseArguments(string[] args)
    {
        if (args.Length < 4)
            return null;

        var config = new ProxyConfiguration
        {
            LocalAddress = args[0],
            LocalPort = int.Parse(args[1]),
            RemoteAddress = args[2],
            RemotePort = int.Parse(args[3]),
            IgnoreSslErrors = false,
            OutputPath = Path.GetTempPath()
        };

        for (int i = 4; i < args.Length; i++)
        {
            switch (args[i].ToLower())
            {
                case "--ignore-ssl":
                case "-k":
                    config.IgnoreSslErrors = true;
                    break;
                case "--output":
                case "-o":
                    if (i + 1 < args.Length)
                    {
                        config.OutputPath = args[++i];
                    }
                    break;
            }
        }

        return config;
    }

    private static void ShowUsage()
    {
        Console.WriteLine("Usage: HttpLogger <local_address> <local_port> <remote_address> <remote_port> [options]");
        Console.WriteLine("Options:");
        Console.WriteLine("  --ignore-ssl, -k    Ignore SSL certificate validation errors");
        Console.WriteLine("  --output, -o <path> Output directory path (default: temp folder)");
        Console.WriteLine();
        Console.WriteLine("Example:");
        Console.WriteLine("  HttpLogger 127.0.0.1 8080 api.example.com 443 --ignore-ssl --output ./logs");
    }
}