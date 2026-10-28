using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

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
        services.AddAuthorization();
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
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwt.SigningKey)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "email",
                    RoleClaimType = "role",
                };
            });

        return services;
    }
}
