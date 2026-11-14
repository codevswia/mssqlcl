using System.Text.RegularExpressions;
using MssqlCl.Models;

namespace MssqlCl.Discovery;

public sealed record CredentialResolution(bool Success, string? Password, string Source, string? Error = null);

/// <summary>
/// Resolves credentials across Windows SSPI, Entra ID MFA, local DPAPI vault, IDE SecretStorage, and environment variables.
/// </summary>
public sealed partial class CredentialResolver
{
    private readonly IdeSecretReader _ideSecrets;
    private readonly LocalVaultStore _localVault;

    public CredentialResolver(IdeSecretReader ideSecrets, LocalVaultStore localVault)
    {
        _ideSecrets = ideSecrets;
        _localVault = localVault;
    }

    public CredentialResolution Resolve(ConnectionProfile profile)
    {
        if (profile.AuthenticationType.Equals("Integrated", StringComparison.OrdinalIgnoreCase))
        {
            return new CredentialResolution(true, null, "WindowsSSPI");
        }

        if (profile.AuthenticationType.Equals("AzureMFA", StringComparison.OrdinalIgnoreCase))
        {
            return new CredentialResolution(true, null, "EntraID_MFA");
        }

        if (!string.IsNullOrEmpty(profile.Password))
        {
            return new CredentialResolution(true, profile.Password, "ProfileJson");
        }

        // 1. Explicit local DPAPI vault override / locally saved profile secret
        var vaultSecret = _localVault.TryGetSecret(profile.Id, profile.DisplayName, $"{profile.Source}:{profile.DisplayName}");
        if (!string.IsNullOrEmpty(vaultSecret))
        {
            return new CredentialResolution(true, vaultSecret, "LocalDpapiVault");
        }

        // 2. IDE SecretStorage (state.vscdb + Local State)
        var ideSecret = _ideSecrets.TryReadSqlPassword(profile);
        if (!string.IsNullOrEmpty(ideSecret))
        {
            return new CredentialResolution(true, ideSecret, $"{profile.Source}:SecretStorage");
        }

        // 3. Environment variable fallback: MSSQLCL_PWD_<SANITIZED_PROFILE_NAME>
        var envKey = "MSSQLCL_PWD_" + SanitizeEnvSuffix(profile.DisplayName);
        var envSecret = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrEmpty(envSecret))
        {
            return new CredentialResolution(true, envSecret, $"Env:{envKey}");
        }

        return new CredentialResolution(
            false,
            null,
            "Missing",
            $"No saved password found for SqlLogin profile '{profile.DisplayName}'. Save it in VS Code / Antigravity IDE or run: mssqlcl conn save --profile \"{profile.DisplayName}\"");
    }

    private static string SanitizeEnvSuffix(string name) =>
        NonAlphanumericRegex().Replace(name, "_").Trim('_').ToUpperInvariant();

    [GeneratedRegex("[^a-zA-Z0-9]+")]
    private static partial Regex NonAlphanumericRegex();
}
