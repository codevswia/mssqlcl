using Microsoft.Data.SqlClient;
using MssqlCl.Models;

namespace MssqlCl.Database;

/// <summary>
/// Builds Microsoft.Data.SqlClient connection strings and initializes Entra ID / Azure MFA authentication.
/// </summary>
public static class SqlConnectionFactory
{
    private static int _azureProviderRegistered;

    public static void EnsureAzureProviderRegistered()
    {
        if (Interlocked.Exchange(ref _azureProviderRegistered, 1) != 0)
        {
            return;
        }

        if (SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryInteractive) is null)
        {
            SqlAuthenticationProvider.SetProvider(
                SqlAuthenticationMethod.ActiveDirectoryInteractive,
                new ActiveDirectoryAuthenticationProvider());
        }
    }

    public static SqlConnectionStringBuilder BuildConnectionString(
        ConnectionProfile profile,
        string? resolvedPassword,
        string? databaseOverride = null)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = profile.Server,
            InitialCatalog = string.IsNullOrWhiteSpace(databaseOverride)
                ? (string.IsNullOrWhiteSpace(profile.Database) ? "master" : profile.Database)
                : databaseOverride,
            ConnectTimeout = profile.ConnectTimeout > 0 ? profile.ConnectTimeout : 30,
            CommandTimeout = profile.CommandTimeout > 0 ? profile.CommandTimeout : 30,
            ApplicationName = "mssqlcl",
            Encrypt = ParseEncryptOption(profile.Encrypt),
            TrustServerCertificate = profile.TrustServerCertificate,
            ApplicationIntent = profile.ApplicationIntent.Equals("ReadOnly", StringComparison.OrdinalIgnoreCase)
                ? ApplicationIntent.ReadOnly
                : ApplicationIntent.ReadWrite
        };

        if (profile.AuthenticationType.Equals("Integrated", StringComparison.OrdinalIgnoreCase))
        {
            builder.IntegratedSecurity = true;
        }
        else if (profile.AuthenticationType.Equals("AzureMFA", StringComparison.OrdinalIgnoreCase))
        {
            EnsureAzureProviderRegistered();
            builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
            var loginHint = !string.IsNullOrWhiteSpace(profile.Email) ? profile.Email : profile.User;
            if (!string.IsNullOrWhiteSpace(loginHint))
            {
                builder.UserID = loginHint;
            }
        }
        else
        {
            builder.IntegratedSecurity = false;
            builder.UserID = profile.User;
            if (!string.IsNullOrEmpty(resolvedPassword))
            {
                builder.Password = resolvedPassword;
            }
        }

        return builder;
    }

    private static SqlConnectionEncryptOption ParseEncryptOption(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return SqlConnectionEncryptOption.Optional;
        }

        if (raw.Equals("Mandatory", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("True", StringComparison.OrdinalIgnoreCase))
        {
            return SqlConnectionEncryptOption.Mandatory;
        }

        if (raw.Equals("Strict", StringComparison.OrdinalIgnoreCase))
        {
            return SqlConnectionEncryptOption.Strict;
        }

        return SqlConnectionEncryptOption.Optional;
    }
}
