using FluentValidation;

namespace Shop.Modules.Users.Auth;

// Shape only; Identity's password policy (UsersModule) checks the password's strength
internal sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public const int MaxEmailLength = 256;
    public const int MaxPasswordLength = 128;

    public RegisterRequestValidator()
    {
        RuleFor(request => request.Email).NotEmpty().EmailAddress().MaximumLength(MaxEmailLength);

        // The cap keeps a huge password from tying up the hasher
        RuleFor(request => request.Password).NotEmpty().MaximumLength(MaxPasswordLength);
    }
}
