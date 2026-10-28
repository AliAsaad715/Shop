using FluentValidation.TestHelper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Users.Auth;
using Shop.Modules.Users.Domain;
using Xunit;

namespace Shop.Modules.Users.UnitTests;

public sealed class RegisterRulesTests : IDisposable
{
    private readonly RegisterRequestValidator _validator = new();
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly UserManager<User> _users;

    public RegisterRulesTests()
    {
        // The module's real Identity options; password checks never open the connection
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Shop"] = "Host=unused" })
            .Build();
        _provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddUsersModule()
            .BuildServiceProvider();
        _scope = _provider.CreateScope();
        _users = _scope.ServiceProvider.GetRequiredService<UserManager<User>>();
    }

    [Fact]
    public void RequestCannotCarryARole() =>
        Assert.DoesNotContain(typeof(RegisterRequest).GetProperties(), property => property.Name.Contains("Role", StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("", "Customer123!", "Email")]
    [InlineData("not-an-email", "Customer123!", "Email")]
    [InlineData("customer@shop.test", "", "Password")]
    public void MalformedRequestsFailValidation(string email, string password, string property) =>
        _validator.TestValidate(new RegisterRequest(email, password)).ShouldHaveValidationErrorFor(property);

    [Fact]
    public void OversizedFieldsFailValidation()
    {
        var result = _validator.TestValidate(new RegisterRequest(
            new string('a', RegisterRequestValidator.MaxEmailLength) + "@shop.test",
            new string('A', RegisterRequestValidator.MaxPasswordLength + 1)));

        result.ShouldHaveValidationErrorFor(request => request.Email);
        result.ShouldHaveValidationErrorFor(request => request.Password);
    }

    [Fact]
    public void WellFormedRequestPassesValidation() =>
        _validator.TestValidate(new RegisterRequest("customer@shop.test", "Customer123!")).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData("Short1!", "PasswordTooShort")]
    [InlineData("customer123!", "PasswordRequiresUpper")]
    [InlineData("CUSTOMER123!", "PasswordRequiresLower")]
    [InlineData("Customerabc!", "PasswordRequiresDigit")]
    [InlineData("Customer1234", "PasswordRequiresNonAlphanumeric")]
    public async Task PasswordPolicyRejectsWeakPasswords(string password, string expectedError)
    {
        var errors = await PasswordErrorsAsync(password);

        Assert.Contains(expectedError, errors);
    }

    [Fact]
    public async Task PasswordPolicyAcceptsAStrongPassword() =>
        Assert.Empty(await PasswordErrorsAsync("Customer123!"));

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }

    private async Task<List<string>> PasswordErrorsAsync(string password)
    {
        var errors = new List<string>();
        foreach (var validator in _users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(_users, new User(), password);
            errors.AddRange(result.Errors.Select(error => error.Code));
        }

        return errors;
    }
}
