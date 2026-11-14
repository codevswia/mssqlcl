using System.Text;
using MssqlCl.Database;
using MssqlCl.Discovery;
using MssqlCl.Models;

namespace MssqlCl.Cli;

/// <summary>
/// Handles administrative CLI subcommands (conn list, conn test, conn save, conn delete) and help output.
/// </summary>
public sealed class CliHandler
{
    private readonly ProfileDiscoveryService _discovery;
    private readonly CredentialResolver _credentials;
    private readonly LocalVaultStore _vault;
    private readonly SessionManager _sessions;

    public CliHandler(
        ProfileDiscoveryService discovery,
        CredentialResolver credentials,
        LocalVaultStore vault,
        SessionManager sessions)
    {
        _discovery = discovery;
        _credentials = credentials;
        _vault = vault;
        _sessions = sessions;
    }

    public async Task<int> ExecuteAsync(string[] args, McpOptions options, CancellationToken ct)
    {
        if (args.Length == 0 || HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            PrintUsage();
            return 0;
        }

        if (HasFlag(args, "--version") || HasFlag(args, "-V"))
        {
            Console.WriteLine("mssqlcl 1.0.0");
            return 0;
        }

        var positional = StripGlobalFlags(args);
        if (positional.Count == 0)
        {
            PrintUsage();
            return 0;
        }

        if (!positional[0].Equals("conn", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown command '{positional[0]}'. Run 'mssqlcl --help' for usage.");
            return 1;
        }

        if (positional.Count < 2)
        {
            Console.Error.WriteLine("Missing subcommand for 'conn'. Use: list, test, save, or delete.");
            return 1;
        }

        return positional[1].ToLowerInvariant() switch
        {
            "list" => ListConnections(positional.Skip(2).FirstOrDefault(), options),
            "test" => await TestConnectionAsync(positional.Skip(2).ToList(), options, ct),
            "save" => SaveConnection(positional.Skip(2).ToList()),
            "delete" => DeleteConnection(positional.Skip(2).ToList()),
            _ => UnknownSubcommand(positional[1])
        };
    }

    private int ListConnections(string? filter, McpOptions options)
    {
        var profiles = _discovery.ListProfiles(options, filter);
        if (profiles.Count == 0)
        {
            Console.WriteLine("No SQL Server profiles found in Antigravity IDE, VS Code, or ~/.mssqlcl.");
            return 0;
        }

        Console.WriteLine($"{"SOURCE",-12} {"STATUS",-18} {"AUTH",-11} {"PROFILE",-28} {"SERVER",-42} {"DB",-18} {"GROUP"}");
        Console.WriteLine(new string('-', 140));

        foreach (var p in profiles)
        {
            var res = _credentials.Resolve(p);
            var status = res.Success ? $"[OK:{res.Source}]" : "[NO SECRET]";
            Console.WriteLine(
                $"{p.Source,-12} {Truncate(status, 18),-18} {Truncate(p.AuthenticationType, 11),-11} " +
                $"{Truncate(p.DisplayName, 28),-28} {Truncate(p.Server, 42),-42} {Truncate(p.Database, 18),-18} {p.GroupPath ?? "-"}");
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {profiles.Count} profile(s). Use 'mssqlcl conn test <profileName>' to verify connectivity.");
        return 0;
    }

    private async Task<int> TestConnectionAsync(List<string> args, McpOptions options, CancellationToken ct)
    {
        if (args.Count == 0)
        {
            Console.Error.WriteLine("Usage: mssqlcl conn test <profileName-or-id> [--db <database>]");
            return 1;
        }

        var target = args[0];
        var dbOverride = GetOptionValue(args, "--db");
        var profile = _discovery.FindProfile(options, target, target, dbOverride);
        if (profile is null)
        {
            Console.Error.WriteLine($"Profile '{target}' not found.");
            return 1;
        }

        Console.WriteLine($"Testing '{profile.DisplayName}' ({profile.Server}, source={profile.Source}, auth={profile.AuthenticationType})...");
        try
        {
            var (session, context) = await _sessions.ConnectAsync(profile, dbOverride, ct);
            Console.WriteLine($"SUCCESS ({session.CredentialSource}) -> {context}");
            await _sessions.DisconnectAsync(session.ConnectionId);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.Message}");
            return 1;
        }
    }

    private int SaveConnection(List<string> args)
    {
        var profileName = GetOptionValue(args, "--profile");
        var server = GetOptionValue(args, "--server");
        var database = GetOptionValue(args, "--database") ?? "master";
        var user = GetOptionValue(args, "--user") ?? string.Empty;
        var auth = GetOptionValue(args, "--auth") ?? "SqlLogin";
        var encrypt = GetOptionValue(args, "--encrypt") ?? "Optional";
        var trustCert = args.Contains("--trust-cert", StringComparer.OrdinalIgnoreCase);
        var savePwd = args.Contains("--savepwd", StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(profileName))
        {
            Console.Error.WriteLine("Usage: mssqlcl conn save --profile <name> [--server <host>] [--database <db>] [--user <user>] [--auth SqlLogin|Integrated|AzureMFA] [--encrypt Optional|Mandatory|Strict] [--trust-cert] [--savepwd]");
            return 1;
        }

        // If --server is omitted, allow saving just the DPAPI password for an existing IDE profile.
        if (string.IsNullOrWhiteSpace(server))
        {
            if (!savePwd && !auth.Equals("SqlLogin", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("--server is required when creating a new local profile.");
                return 1;
            }

            var pwdOnly = PromptPassword($"Password for '{profileName}': ");
            _vault.SaveSecret(profileName, pwdOnly);
            Console.WriteLine($"Saved DPAPI-protected password in local vault for '{profileName}'.");
            return 0;
        }

        var profile = new ConnectionProfile
        {
            Id = Guid.NewGuid().ToString(),
            ProfileName = profileName,
            Server = server,
            Database = database,
            User = user,
            AuthenticationType = auth,
            Encrypt = encrypt,
            TrustServerCertificate = trustCert,
            SavePassword = savePwd,
            Source = "local"
        };

        _vault.SaveProfile(profile);

        if (auth.Equals("SqlLogin", StringComparison.OrdinalIgnoreCase) && savePwd)
        {
            var pwd = PromptPassword($"Password for '{profileName}' ({user}@{server}): ");
            _vault.SaveSecret(profile.Id, pwd);
        }

        Console.WriteLine($"Saved local profile '{profile.DisplayName}' (id={profile.Id}).");
        return 0;
    }

    private int DeleteConnection(List<string> args)
    {
        if (args.Count == 0)
        {
            Console.Error.WriteLine("Usage: mssqlcl conn delete <profileName-or-id>");
            return 1;
        }

        var ok = _vault.DeleteProfile(args[0]);
        Console.WriteLine(ok ? $"Deleted local profile '{args[0]}'." : $"Local profile '{args[0]}' not found.");
        return ok ? 0 : 1;
    }

    private static int UnknownSubcommand(string sub)
    {
        Console.Error.WriteLine($"Unknown 'conn' subcommand '{sub}'. Use: list, test, save, or delete.");
        return 1;
    }

    private static string PromptPassword(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
            {
                sb.Remove(sb.Length - 1, 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }
        return sb.ToString();
    }

    private static List<string> StripGlobalFlags(string[] args)
    {
        var list = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "--mcp" or "-mcp" or "--readonly" or "--readwrite" or "--no-audit")
            {
                continue;
            }
            if (a is "--max-rows" or "--source" or "--timeout")
            {
                i++;
                continue;
            }
            list.Add(a);
        }
        return list;
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetOptionValue(List<string> args, string flag)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static string Truncate(string? value, int maxLen)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }
        return value.Length <= maxLen ? value : value[..(maxLen - 1)] + "…";
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            mssqlcl - SQL Server MCP Server & CLI (inspired by Oracle SQLcl)

            USAGE:
              mssqlcl --mcp [options]                 Start MCP server over stdio
              mssqlcl conn list [filter] [options]    List discovered VS Code / Antigravity / local profiles
              mssqlcl conn test <profile> [--db db]   Test live SQL Server connectivity
              mssqlcl conn save --profile <name> ...  Save a local profile or DPAPI password override
              mssqlcl conn delete <profile>           Delete a local profile from ~/.mssqlcl

            OPTIONS:
              --readonly              Block write/DDL/EXEC queries (default)
              --readwrite             Allow DML/DDL/EXEC queries in mssql_run_query
              --max-rows <N>          Max rows returned per query (default: 500)
              --timeout <seconds>     Default SQL command timeout (default: 30)
              --source <mode>         auto | antigravity | vscode | local (default: auto)
              --no-audit              Disable ~/.mssqlcl/audit.log
              -V, --version           Print version
              -h, --help              Show help
            """);
    }
}
