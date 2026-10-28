using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shop.Modules.Users.Domain;

namespace Shop.Modules.Users.Auth;

internal sealed class JwtTokenIssuer(IOptions<JwtOptions> settings, TimeProvider clock)
{
    public LoginResponse Issue(User user, IList<string> roles)
    {
        var options = settings.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(options.LifetimeMinutes),
            TokenType = "JWT",
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(options.SigningKey)), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = user.Id.ToString(),
                ["email"] = user.Email!,
                ["role"] = roles.ToArray(),
                ["jti"] = Guid.NewGuid().ToString(),
            },
        });

        return new LoginResponse(token, "Bearer", options.LifetimeMinutes * 60);
    }
}
