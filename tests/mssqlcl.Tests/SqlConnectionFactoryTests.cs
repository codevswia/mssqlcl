using Microsoft.Data.SqlClient;
using MssqlCl.Database;
using MssqlCl.Models;

namespace MssqlCl.Tests;

public class SqlConnectionFactoryTests
{
    [Fact]
    public void EnsureAzureProviderRegistered_ConfiguresActiveDirectoryInteractiveProvider()
    {
        SqlConnectionFactory.EnsureAzureProviderRegistered();

        var provider = SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryInteractive);
        Assert.IsType<ActiveDirectoryAuthenticationProvider>(provider);
    }

    [Fact]
    public void BuildConnectionString_AzureMfa_SetsInteractiveAuthAndLoginHint()
    {
        var profile = new ConnectionProfile
        {
            Id = "test-azure-mfa",
            ProfileName = "AzureMfaProfile",
            Server = "myserver.database.windows.net",
            Database = "mydb",
            User = "user@contoso.com",
            AuthenticationType = "AzureMFA",
            Encrypt = "Mandatory"
        };

        var builder = SqlConnectionFactory.BuildConnectionString(profile, resolvedPassword: null);

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryInteractive, builder.Authentication);
        Assert.Equal("user@contoso.com", builder.UserID);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
    }
}
