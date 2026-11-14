using System.Text.Encodings.Web;
using System.Text.Json;
using MssqlCl.Discovery;
using MssqlCl.Models;

namespace MssqlCl.Security;

/// <summary>
/// Writes structured JSON-lines audit events to ~/.mssqlcl/audit.log without ever persisting secrets.
/// </summary>
public sealed class AuditLogger
{
    private static readonly JsonSerializerOptions LogJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _logPath;
    private readonly bool _enabled;
    private readonly Lock _sync = new();

    public AuditLogger(IdeSourceLocator locator, McpOptions options)
    {
        _logPath = Path.Combine(locator.LocalStoreDir, "audit.log");
        _enabled = options.AuditEnabled;
    }

    public void Log(string action, string status, string? connectionId = null, string? profileName = null, string? server = null, string? database = null, string? detail = null)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            var entry = new
            {
                ts = DateTimeOffset.UtcNow.ToString("O"),
                action,
                status,
                connectionId,
                profile = profileName,
                server,
                database,
                detail
            };

            var line = JsonSerializer.Serialize(entry, LogJsonOptions);
            lock (_sync)
            {
                var dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mssqlcl] Audit write failed: {ex.Message}");
        }
    }
}
