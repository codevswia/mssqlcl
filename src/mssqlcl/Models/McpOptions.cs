namespace MssqlCl.Models;

/// <summary>
/// Runtime configuration parsed from command-line flags for both CLI and MCP server modes.
/// </summary>
public sealed class McpOptions
{
    public bool ReadOnly { get; set; } = true;
    public int MaxRows { get; set; } = 500;
    public int DefaultQueryTimeoutSeconds { get; set; } = 30;
    public string SourceFilter { get; set; } = "auto";
    public bool AuditEnabled { get; set; } = true;

    /// <summary>
    /// Resolves the ordered list of profile sources to scan based on <see cref="SourceFilter"/>.
    /// </summary>
    public IReadOnlyList<string> GetSourcePriority() => SourceFilter.ToLowerInvariant() switch
    {
        "antigravity" => ["antigravity", "local"],
        "vscode" => ["vscode", "local"],
        "local" => ["local"],
        _ => ["antigravity", "vscode", "local"]
    };
}
