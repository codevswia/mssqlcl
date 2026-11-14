namespace MssqlCl.Discovery;

/// <summary>
/// Resolves standard user data directories for supported IDEs and local mssqlcl storage.
/// </summary>
public sealed class IdeSourceLocator
{
    private readonly IReadOnlyDictionary<string, string> _ideDirectories;
    private readonly string _localStoreDir;

    public IdeSourceLocator()
        : this(GetDefaultIdeDirectories(), GetDefaultLocalStoreDir())
    {
    }

    public IdeSourceLocator(IReadOnlyDictionary<string, string> ideDirectories, string localStoreDir)
    {
        _ideDirectories = ideDirectories;
        _localStoreDir = localStoreDir;
    }

    public string LocalStoreDir => _localStoreDir;

    public string? GetIdeDirectory(string sourceName) =>
        _ideDirectories.TryGetValue(sourceName, out var dir) ? dir : null;

    private static IReadOnlyDictionary<string, string> GetDefaultIdeDirectories()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["antigravity"] = Path.Combine(appData, "Antigravity IDE"),
            ["vscode"] = Path.Combine(appData, "Code")
        };
    }

    private static string GetDefaultLocalStoreDir()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".mssqlcl");
    }
}
