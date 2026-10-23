using SyWater.Notifications.Domain.Emails;
using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Application.Ports.Out;

/// <summary>
/// Where to write to a user. Comes from ms-iam; nothing is copied into our database. The phone (E.164) is usable for
/// SMS / WhatsApp only when it is verified, and only by the mode it was verified with (HU-028).
/// </summary>
public sealed record UserContact(
    Guid UserId,
    string Email,
    string? FullName,
    string? Phone = null,
    bool PhoneVerified = false,
    ShortMessageChannel? PhoneChannel = null)
{
    /// <summary>The verified phone and its mode, or null when there is nowhere to send a short message.</summary>
    public (string Phone, ShortMessageChannel Channel)? VerifiedPhone =>
        PhoneVerified && PhoneNumbers.IsE164(Phone) && PhoneChannel is { } channel ? (Phone!, channel) : null;
}

/// <summary>A finished mail, ready for the SMTP server.</summary>
public sealed record EmailMessage(string ToAddress, string? ToName, string Subject, string TextBody, string HtmlBody);

/// <summary>Asks ms-iam (with the internal service key) for the e-mail of a user.</summary>
public interface IUserContactDirectory
{
    /// <returns>null if the user does not exist any more or has no e-mail (retrying will not help).</returns>
    /// <exception cref="ExternalServiceUnavailableException">ms-iam did not answer: try again later.</exception>
    Task<UserContact?> FindAsync(Guid userId, CancellationToken ct);
}

/// <summary>Real SMTP in production, Mailpit in development.</summary>
public interface IEmailSender
{
    /// <exception cref="EmailSendException">The SMTP server refused or could not be reached: try again later.</exception>
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public sealed class EmailSendException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>ms-iam is down or answered with an unexpected error. Try again later.</summary>
public sealed class ExternalServiceUnavailableException(string service, Exception? inner = null)
    : Exception($"The service '{service}' is not available right now.", inner)
{
    public string Service { get; } = service;
}

/// <summary>Persistence of the mails waiting to leave.</summary>
public interface IEmailOutboxRepository
{
    /// <summary>False if the same source event already queued a mail for that user (redelivered event).</summary>
    Task<bool> AddAsync(EmailOutboxItem item, CancellationToken ct);

    /// <summary>PENDING mails whose time came, oldest first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<EmailOutboxItem>> GetDueAsync(DateTime now, int max, CancellationToken ct);

    /// <summary>Saves the outcome, only if the stored mail is still PENDING (two workers cannot both finish it).</summary>
    Task<bool> SaveOutcomeAsync(EmailOutboxItem item, CancellationToken ct);
}

/// <summary>Settings of the e-mail channel that the use cases need (not the SMTP ones).</summary>
public sealed record EmailSettings(bool Enabled, string AppBaseUrl);

/// <summary>HU-013/HU-032: how long a device may stay silent before it is "disconnected" (default 10 minutes).</summary>
public sealed record OfflineSettings(TimeSpan After)
{
    public static OfflineSettings Default { get; } = new(TimeSpan.FromMinutes(10));
}
