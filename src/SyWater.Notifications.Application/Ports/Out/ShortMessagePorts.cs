using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Application.Ports.Out;

/// <summary>A finished short message: E.164 phone, mode and text (the adapter adapts the text to SMS when needed).</summary>
public sealed record ShortMessage(string ToPhone, ShortMessageChannel Channel, string Body);

/// <summary>Twilio in production, Mailpit in development (each message becomes a mail to look at).</summary>
public interface IShortMessageSender
{
    /// <returns>The id the provider gave to the message (Twilio SID), for traceability.</returns>
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
