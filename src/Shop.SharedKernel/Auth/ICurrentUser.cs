namespace Shop.SharedKernel.Auth;

/// <summary>The authenticated request's identity, supplied by validated token claims.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    string? Email { get; }
    IReadOnlyCollection<string> Roles { get; }
    bool IsInRole(string role);
}
