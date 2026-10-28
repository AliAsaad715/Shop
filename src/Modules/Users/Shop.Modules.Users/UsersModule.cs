using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Users.Data;
using Shop.SharedKernel.Persistence;

namespace Shop.Modules.Users;

public static class UsersModule
{
    public static IServiceCollection AddUsersModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<UsersDbContext>(UsersDbContext.Schema);

        return services;
    }

    public static IEndpointRouteBuilder MapUsersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // POST /auth/register, POST /auth/login and GET /users/me arrive with tasks 09 and 10

        return endpoints;
    }
}
