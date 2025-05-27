using HttpLogger.Core;
using Spectre.Console;

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

            // Create a nice banner
            var rule = new Rule("[bold blue]HTTP Logger Proxy[/]")
            {
                Justification = Justify.Center
            };
            AnsiConsole.Write(rule);

            // Display configuration in a table
            var table = new Table();
            table.AddColumn("[bold]Setting[/]");
            table.AddColumn("[bold]Value[/]");
            table.AddRow("Local Address", $"[cyan]{config.LocalAddress}:{config.LocalPort}[/]");
            table.AddRow("Remote Address", $"[yellow]{config.RemoteAddress}:{config.RemotePort}[/]");
            table.AddRow("Output Directory", $"[green]{config.OutputPath}[/]");
            table.AddRow("Ignore SSL Errors", config.IgnoreSslErrors ? "[red]Yes[/]" : "[green]No[/]");
            
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold green]Press Ctrl+C to stop...[/]");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                AnsiConsole.MarkupLine("\n[red]Shutting down...[/]");
            };

            await proxyServer.StartAsync(cts.Token);
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
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
        var rule = new Rule("[bold red]Usage Information[/]")
        {
            Justification = Justify.Center
        };
        AnsiConsole.Write(rule);

        AnsiConsole.MarkupLine("[bold]Usage:[/] HttpLogger [cyan]<local_address> <local_port> <remote_address> <remote_port>[/] [dim][options][/]");
        AnsiConsole.WriteLine();
        
        var table = new Table();
        table.AddColumn("[bold]Option[/]");
        table.AddColumn("[bold]Description[/]");
        table.AddRow("[cyan]--ignore-ssl, -k[/]", "Ignore SSL certificate validation errors");
        table.AddRow("[cyan]--output, -o <path>[/]", "Output directory path (default: temp folder)");
        
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        
        AnsiConsole.MarkupLine("[bold]Example:[/]");
        AnsiConsole.MarkupLine("  [dim]HttpLogger 127.0.0.1 8080 api.example.com 443 --ignore-ssl --output ./logs[/]");
    }
}