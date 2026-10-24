using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Application.Ports.Out;

/// <summary>What a short message is. The WhatsApp adapter of Meta picks the approved template from it (a code goes with the
/// authentication template, the rest with the general one); SMS ignores it.</summary>
public enum ShortMessageKind { Notice, VerificationCode }

/// <summary>
/// A finished short message: E.164 phone, mode and text (the adapter adapts the text to SMS when needed). Code is the bare
/// verification code when <see cref="Kind"/> is VerificationCode.
/// </summary>
public sealed record ShortMessage(string ToPhone, ShortMessageChannel Channel, string Body,
    ShortMessageKind Kind = ShortMessageKind.Notice, string? Code = null);

/// <summary>WhatsApp Cloud API of Meta and / or Twilio in production, Mailpit in development (each message becomes a mail to look at).</summary>
public interface IShortMessageSender
{
    /// <returns>The id the provider gave to the message (Meta wamid, Twilio SID), for traceability.</returns>
    /// <exception cref="ShortMessageSendException">The provider failed for now: try again later.</exception>
    /// <exception cref="ShortMessageRejectedException">The provider refused this number or mode for good.</exception>
    Task<string?> SendAsync(ShortMessage message, CancellationToken ct);
}

/// <summary>The provider did not answer, answered 429 / 5xx, or the network failed. Retrying makes sense.</summary>
public sealed class ShortMessageSendException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The provider refused the message for good: the number is not valid, is not a mobile, is not on WhatsApp... Retrying
/// the same message will not help. HTTP 422 for the verification code, so the user can fix the number or the mode.
/// </summary>
public sealed class ShortMessageRejectedException(string reason, string? providerCode = null) : Exception(reason)
{
    public string? ProviderCode { get; } = providerCode;
}

/// <summary>SMS / WhatsApp is off (no provider configured). HTTP 503 for the verification code.</summary>
public sealed class ShortMessagesDisabledException()
    : Exception("SMS / WhatsApp is not configured in this environment.");

/// <summary>Persistence of the short messages waiting to leave.</summary>
public interface ISmsOutboxRepository
{
    /// <summary>False if the same source event already queued a message for that user (redelivered event).</summary>
    Task<bool> AddAsync(SmsOutboxItem item, CancellationToken ct);

    /// <summary>PENDING messages whose time came, oldest first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<SmsOutboxItem>> GetDueAsync(DateTime now, int max, CancellationToken ct);

    /// <summary>Saves the outcome, only if the stored message is still PENDING (two workers cannot both finish it).</summary>
    Task<bool> SaveOutcomeAsync(SmsOutboxItem item, CancellationToken ct);
}

/// <summary>Whether the SMS / WhatsApp channel is on (a provider is configured).</summary>
public sealed record SmsSettings(bool Enabled);

/// <summary>
/// What the provider says about a message after accepting it. Twilio takes a WhatsApp at once and reports later that it
/// failed (free text outside the 24 h window, a number without WhatsApp...): this is how the admin's test finds out.
/// </summary>
public sealed record ShortMessageStatus(string Status, bool Failed, bool Final, string? Error)
{
    public static ShortMessageStatus From(string status, string? error)
    {
        var s = (status ?? "").Trim().ToLowerInvariant();
        var failed = s is "failed" or "undelivered" or "canceled";
        return new ShortMessageStatus(s, failed, failed || s is "delivered" or "read", error);
    }
}

/// <summary>Reads the delivery status of a message the provider already accepted (only Twilio offers it here).</summary>
public interface IShortMessageStatusReader
{
    /// <returns>Null when the id is not of this provider or it cannot be read right now.</returns>
    Task<ShortMessageStatus?> ReadAsync(string providerMessageId, CancellationToken ct);
}
