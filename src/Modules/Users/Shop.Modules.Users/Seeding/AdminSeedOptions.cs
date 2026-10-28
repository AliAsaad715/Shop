namespace Shop.Modules.Users.Seeding;

// Bound from the Admin section, e.g. the Admin__Email and Admin__Password environment variables
internal sealed class AdminSeedOptions
{
    public const string Section = "Admin";

    public string? Email { get; init; }

    public string? Password { get; init; }
}
