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

            // Display configuration in a panel (now with actual session path)
            var configText = new StringBuilder();
            configText.AppendLine($"[cyan]Local:[/] {config.LocalAddress}:{config.LocalPort}");
            configText.AppendLine($"[yellow]Remote:[/] {config.RemoteAddress}:{config.RemotePort}");
            configText.AppendLine($"[green]Session Directory:[/] {logger.SessionPath}");
            configText.AppendLine($"[dim]SSL Validation:[/] {(config.IgnoreSslErrors ? "[red]Disabled[/]" : "[green]Enabled[/]")}");

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
        
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        
        AnsiConsole.MarkupLine("[bold]Example:[/]");
        AnsiConsole.MarkupLine("  HttpLogger 127.0.0.1 8080 api.example.com 443 --ignore-ssl --output ./logs");
    }
}
