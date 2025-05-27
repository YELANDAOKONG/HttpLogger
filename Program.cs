using HttpLogger.Core;
using Spectre.Console;
using System.Text;

namespace HttpLogger;

static class Program
{
    public static async Task Main(string[] args)
    {
        RequestResponseLogger? logger = null;
        ProxyServer? proxyServer = null;
        
        try
        {
            var config = ParseArguments(args);
            if (config == null)
            {
                ShowUsage();
                return;
            }

            logger = new RequestResponseLogger(config.OutputPath);
            proxyServer = new ProxyServer(config, logger);

            // Create a nice banner
            AnsiConsole.Write(new Rule("[bold blue]HTTP Logger Proxy[/]"));

            // Display configuration in a panel
            var configText = new StringBuilder();
            configText.AppendLine($"[cyan]Local:[/] {config.LocalAddress}:{config.LocalPort}");
            configText.AppendLine($"[yellow]Remote:[/] {config.RemoteAddress}:{config.RemotePort}");
            configText.AppendLine($"[green]Session Directory:[/] {logger.SessionPath}");
            configText.AppendLine($"[dim]SSL Validation:[/] {(config.IgnoreSslErrors ? "[red]Disabled[/]" : "[green]Enabled[/]")}");
            configText.AppendLine($"[dim]Timeout:[/] [yellow]{config.TimeoutSeconds}s[/]");
            
            // Streaming configuration display
            if (config.EnableStreaming)
            {
                configText.AppendLine($"[dim]Streaming Mode:[/] [green]Enabled[/] (Buffer: {config.StreamBufferSize} bytes)");
                configText.AppendLine($"[dim]Max Full Log Size:[/] [yellow]{config.MaxFullLogSize / (1024 * 1024)}MB[/]");
            }
            else
            {
                configText.AppendLine($"[dim]Streaming Mode:[/] [red]Disabled[/] (Full buffering)");
            }

            var panel = new Panel(configText.ToString())
            {
                Header = new PanelHeader("[bold]Configuration[/]"),
                Border = BoxBorder.Rounded,
                Padding = new Padding(1, 0, 1, 0)
            };
            
            AnsiConsole.Write(panel);
            AnsiConsole.MarkupLine("[bold green]Press Ctrl+C to stop...[/]");
            AnsiConsole.WriteLine();

            using var cts = new CancellationTokenSource();
            var shutdownRequested = false;
            
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                if (!shutdownRequested)
                {
                    shutdownRequested = true;
                    cts.Cancel();
                    AnsiConsole.MarkupLine("\n[yellow]Stopping server gracefully...[/]");
                }
                else
                {
                    // Force exit if user presses Ctrl+C again
                    AnsiConsole.MarkupLine("\n[red]Force stopping...[/]");
                    Environment.Exit(1);
                }
            };

            // Start server with timeout for graceful shutdown
            var serverTask = proxyServer.StartAsync(cts.Token);
            
            // Wait for server to complete or timeout
            var completedTask = await Task.WhenAny(
                serverTask,
                Task.Delay(Timeout.Infinite, cts.Token)
            );

            if (completedTask == serverTask)
            {
                await serverTask; // Propagate any exceptions
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
            AnsiConsole.MarkupLine("[dim]Shutdown completed.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            Environment.Exit(1);
        }
        finally
        {
            // Ensure resources are disposed properly
            try
            {
                if (logger != null)
                {
                    AnsiConsole.MarkupLine("[dim]Flushing logs...[/]");
                    await logger.FlushAsync();
                    logger.Dispose();
                }
                
                proxyServer?.Dispose();
                AnsiConsole.MarkupLine("[dim]Cleanup completed.[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error during cleanup: {ex.Message}[/]");
            }
        }
    }

    private static ProxyConfiguration? ParseArguments(string[] args)
    {
        if (args.Length < 4)
            return null;

        try
        {
            var config = new ProxyConfiguration
            {
                LocalAddress = args[0],
                LocalPort = int.Parse(args[1]),
                RemoteAddress = args[2],
                RemotePort = int.Parse(args[3]),
                IgnoreSslErrors = false,
                OutputPath = Path.GetTempPath(),
                TimeoutSeconds = 180, // Default 3 minutes
                EnableStreaming = false,
                StreamBufferSize = 8192, // Default 8KB
                MaxFullLogSize = 10 * 1024 * 1024 // Default 10MB
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
                    case "--timeout":
                    case "-t":
                        if (i + 1 < args.Length)
                        {
                            if (int.TryParse(args[++i], out var timeoutSeconds) && timeoutSeconds > 0)
                            {
                                config.TimeoutSeconds = timeoutSeconds;
                            }
                            else
                            {
                                AnsiConsole.MarkupLine("[red]Error: Invalid timeout value. Must be a positive integer.[/]");
                                return null;
                            }
                        }
                        break;
                    case "--stream":
                    case "-s":
                        config.EnableStreaming = true;
                        // Check if next argument is buffer size
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out var bufferSize) && bufferSize > 0)
                        {
                            config.StreamBufferSize = bufferSize;
                            i++; // Skip the buffer size argument
                        }
                        break;
                    case "--max-log-size":
                        if (i + 1 < args.Length)
                        {
                            if (long.TryParse(args[++i], out var maxSize) && maxSize > 0)
                            {
                                config.MaxFullLogSize = maxSize * 1024 * 1024; // Convert MB to bytes
                            }
                            else
                            {
                                AnsiConsole.MarkupLine("[red]Error: Invalid max log size. Must be a positive integer (MB).[/]");
                                return null;
                            }
                        }
                        break;
                }
            }

            return config;
        }
        catch (FormatException)
        {
            AnsiConsole.MarkupLine("[red]Error: Invalid port number format[/]");
            return null;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error parsing arguments: {ex.Message}[/]");
            return null;
        }
    }

    private static void ShowUsage()
    {
        AnsiConsole.Write(new Rule("[bold red]Usage Information[/]"));
        
        AnsiConsole.Write(new Markup("[bold]Usage:[/] HttpLogger "));
        AnsiConsole.Write(new Markup("[cyan]<local_address> <local_port> <remote_address> <remote_port>[/] "));
        AnsiConsole.WriteLine("[options]");
        AnsiConsole.WriteLine();
        
        var table = new Table();
        table.AddColumn("[bold]Option[/]");
        table.AddColumn("[bold]Description[/]");
        table.AddRow("[cyan]--ignore-ssl, -k[/]", "Ignore SSL certificate validation errors");
        table.AddRow("[cyan]--output, -o <path>[/]", "Output directory path (default: temp folder)");
        table.AddRow("[cyan]--timeout, -t <seconds>[/]", "Request timeout in seconds (default: 180)");
        table.AddRow("[cyan]--stream, -s [buffer_size][/]", "Enable streaming mode with optional buffer size (default: 8192 bytes)");
        table.AddRow("[cyan]--max-log-size <mb>[/]", "Maximum size (MB) for full content logging in streaming mode (default: 10)");
        
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        
        AnsiConsole.MarkupLine("[bold]Examples:[/]");
        AnsiConsole.MarkupLine("  HttpLogger 127.0.0.1 8080 api.example.com 443 --ignore-ssl --timeout 300 --output ./logs");
        AnsiConsole.MarkupLine("  HttpLogger 127.0.0.1 8080 api.example.com 443 --stream 4096 --max-log-size 50");
    }
}
