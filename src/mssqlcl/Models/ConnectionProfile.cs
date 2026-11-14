namespace MssqlCl.Models;

/// <summary>
/// Represents a discovered or locally stored SQL Server connection profile.
/// </summary>
public sealed class ConnectionProfile
{
    public string Id { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string? Password { get; set; }
    public string AuthenticationType { get; set; } = "SqlLogin";
    public string Encrypt { get; set; } = "Optional";
    public bool TrustServerCertificate { get; set; }
    public int ConnectTimeout { get; set; } = 30;
    public int CommandTimeout { get; set; } = 30;
    public string ApplicationIntent { get; set; } = "ReadWrite";
    public string? GroupId { get; set; }
    public string? GroupPath { get; set; }
    public string? TenantId { get; set; }
    public string? Email { get; set; }
    public string? AccountId { get; set; }
    public bool SavePassword { get; set; }

    /// <summary>
    /// Origin identifier: "antigravity", "vscode", or "local".
    /// </summary>
    public string Source { get; set; } = "local";

    /// <summary>
    /// Root user data folder of the originating IDE (used to locate Local State and state.vscdb).
    /// </summary>
    public string? SourceDataDir { get; set; }

    /// <summary>
    /// Human-readable display name matching ms-mssql conventions.
    /// </summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(ProfileName)
            ? ProfileName
            : string.IsNullOrWhiteSpace(Database) ? Server : $"{Server}\\{Database}";
}

/// <summary>
/// Represents a hierarchical connection group entry from ms-mssql.connectionGroups.
/// </summary>
public sealed class ConnectionGroup
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ParentId { get; set; }
}
