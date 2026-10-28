using Microsoft.AspNetCore.Identity;

namespace Shop.Modules.Users.Domain;

internal sealed class User : IdentityUser<Guid>
{
    // UUIDv7 can't be guessed and sorts by creation time
    public User() => Id = Guid.CreateVersion7();
}
