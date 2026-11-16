namespace Shop.Modules.Notifications.Email;

/// <summary>A plain-text email to one recipient.</summary>
internal sealed record EmailMessage(string To, string Subject, string TextBody);
