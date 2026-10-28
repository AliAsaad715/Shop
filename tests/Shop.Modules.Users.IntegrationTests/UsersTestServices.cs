using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shop.Modules.Users.IntegrationTests;

// The Users module's real registrations, pointed at a test database
internal static class UsersTestServices
{
    public static ServiceProvider Build(string connectionString, string? adminEmail = null, string? adminPassword = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Shop"] = connectionString,
                ["Admin:Email"] = adminEmail,
                ["Admin:Password"] = adminPassword,
            })
            .Build();

        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddUsersModule()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
