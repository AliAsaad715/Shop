using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Users.Auth;
using Shop.Modules.Users.Data;
using Shop.Modules.Users.Domain;
using Shop.Modules.Users.Seeding;
using Shop.SharedKernel.Persistence;

namespace Shop.Modules.Users;

public static class UsersModule
{
    public static IServiceCollection AddUsersModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<UsersDbContext>(UsersDbContext.Schema);

        // Identity Core only: user and role stores plus password hashing, no cookie UI or MapIdentityApi
        services.AddIdentityCore<User>(options =>
            {
                options.User.RequireUniqueEmail = true;

                // The user name is the email, already checked by RegisterRequestValidator
                options.User.AllowedUserNameCharacters = string.Empty;

                // Identity's defaults already require upper and lower case, a digit and a symbol
                options.Password.RequiredLength = 8;
            })
            .AddRoles<Role>()
            .AddEntityFrameworkStores<UsersDbContext>()
            .AddSignInManager();

        services.AddUsersJwt();

        services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>(includeInternalTypes: true);

        services.AddOptions<AdminSeedOptions>().BindConfiguration(AdminSeedOptions.Section);
        services.AddHostedService<AdminSeeder>();

        return services;
    }

    public static IEndpointRouteBuilder MapUsersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RegisterEndpoint.Map(endpoints);
        LoginEndpoint.Map(endpoints);
        CurrentUserEndpoint.Map(endpoints);

        return endpoints;
    }
}
