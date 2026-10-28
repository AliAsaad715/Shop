using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Shop.SharedKernel.Auth;

namespace Shop.Modules.Users.Auth;

internal static class JwtAuthentication
{
    public static IServiceCollection AddUsersJwt(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>()
            .BindConfiguration(JwtOptions.Section)
            .Validate(options => !string.IsNullOrWhiteSpace(options.Issuer), "Jwt:Issuer is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Audience), "Jwt:Audience is required.")
            .Validate(options => options.LifetimeMinutes is >= 15 and <= 30, "Jwt:LifetimeMinutes must be between 15 and 30.")
            .Validate(JwtOptions.HasValidSigningKey, "Set Jwt__SigningKey to a base64-encoded random key of at least 32 bytes.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<JwtTokenIssuer>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddAuthorization(options =>
        {
            var authenticated = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().Build();
            options.DefaultPolicy = authenticated;
            options.FallbackPolicy = authenticated;
            options.AddPolicy(Policies.AdminOnly, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireRole(Roles.Admin));
            options.AddPolicy(Policies.CustomerOnly, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireRole(Roles.Customer));
        });
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, settings) =>
            {
                var jwt = settings.Value;
                bearer.MapInboundClaims = false;
                bearer.IncludeErrorDetails = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    RequireAudience = true,
                    ValidAudience = jwt.Audience,
                    IgnoreTrailingSlashWhenValidatingAudience = false,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwt.SigningKey)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidTypes = ["JWT"],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "email",
                    RoleClaimType = "role",
                };
                bearer.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var principal = context.Principal!;
                        var subjects = principal.FindAll("sub").ToArray();
                        var emails = principal.FindAll("email").ToArray();
                        var tokenIds = principal.FindAll("jti").ToArray();
                        var roles = principal.FindAll("role").ToArray();
                        if (subjects.Length != 1 || !Guid.TryParse(subjects[0].Value, out var id) || id == Guid.Empty
                            || emails.Length != 1 || string.IsNullOrWhiteSpace(emails[0].Value)
                            || tokenIds.Length != 1 || string.IsNullOrWhiteSpace(tokenIds[0].Value)
                            || roles.Length == 0 || roles.Any(role => string.IsNullOrWhiteSpace(role.Value)))
                        {
                            context.Fail("Invalid token claims.");
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        return services;
    }
}
