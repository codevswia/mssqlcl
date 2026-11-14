using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using MssqlCl.Cli;
using MssqlCl.Database;
using MssqlCl.Discovery;
using MssqlCl.Mcp;
using MssqlCl.Models;
using MssqlCl.Security;

namespace MssqlCl;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = ParseOptions(args);
        var isMcpMode = args.Any(a => a.Equals("--mcp", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("-mcp", StringComparison.OrdinalIgnoreCase));

        if (!isMcpMode)
        {
            var locator = new IdeSourceLocator();
            var vault = new LocalVaultStore(locator);
            var discovery = new ProfileDiscoveryService(locator, vault);
            var creds = new CredentialResolver(new IdeSecretReader(), vault);
            await using var sessions = new SessionManager(creds);
            var cli = new CliHandler(discovery, creds, vault, sessions, locator);
            return await cli.ExecuteAsync(args, options, CancellationToken.None);
        }

        Console.Error.WriteLine($"[mssqlcl] Starting MCP stdio server (mode={(options.ReadOnly ? "readonly" : "readwrite")}, maxRows={options.MaxRows}, source={options.SourceFilter})");

        var builder = Host.CreateApplicationBuilder(args);

        // Critical for MCP stdio transport: never emit logs to stdout.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(consoleOpts =>
        {
            consoleOpts.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IdeSourceLocator>();
        builder.Services.AddSingleton<LocalVaultStore>();
        builder.Services.AddSingleton<ProfileDiscoveryService>();
        builder.Services.AddSingleton<IdeSecretReader>();
        builder.Services.AddSingleton<CredentialResolver>();
        builder.Services.AddSingleton<AuditLogger>();
        builder.Services.AddSingleton<SessionManager>();

        builder.Services
            .AddMcpServer(serverOpts =>
            {
                serverOpts.ServerInfo = new Implementation
                {
                    Name = "mssqlcl",
                    Title = "SQL Server MCP Server (mssqlcl)",
                    Version = "1.0.0"
                };
                serverOpts.ServerInstructions =
                    "SQL Server MCP server modeled after Oracle SQLcl and ms-mssql. " +
                    "Call mssql_list_servers first to discover saved connections, then mssql_connect to obtain a connectionId. " +
                    "Query results are returned in compact CSV by default. Always call mssql_disconnect when finished.";
            })
            .WithStdioServerTransport()
            .WithTools<MssqlTools>();

        var app = builder.Build();
        await app.RunAsync();
        return 0;
    }

    internal static McpOptions ParseOptions(string[] args)
    {
        var opts = new McpOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--readonly", StringComparison.OrdinalIgnoreCase))
            {
                opts.ReadOnly = true;
            }
            else if (arg.Equals("--readwrite", StringComparison.OrdinalIgnoreCase))
            {
                opts.ReadOnly = false;
            }
            else if (arg.Equals("--no-audit", StringComparison.OrdinalIgnoreCase))
            {
                opts.AuditEnabled = false;
            }
            else if (arg.Equals("--max-rows", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var maxRows) && maxRows > 0)
            {
                opts.MaxRows = maxRows;
                i++;
            }
            else if (arg.Equals("--timeout", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var timeout) && timeout > 0)
            {
                opts.DefaultQueryTimeoutSeconds = timeout;
                i++;
            }
            else if (arg.Equals("--source", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                opts.SourceFilter = args[i + 1];
                i++;
            }
        }
        return opts;
    }
}
