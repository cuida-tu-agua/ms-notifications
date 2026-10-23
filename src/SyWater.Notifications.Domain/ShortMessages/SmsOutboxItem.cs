namespace SyWater.Notifications.Domain.ShortMessages;

/// <summary>
/// PENDING → SENT (left by SMS or WhatsApp) / FAILED (attempts ran out, or the provider refused the number for good) /
/// SKIPPED (when it was time to send, the user had no verified phone: nothing to retry).
/// </summary>
public enum SmsStatus { Pending, Sent, Failed, Skipped }

/// <summary>How hard we try before giving up: 5 attempts, waiting 1 min, 5 min, 15 min and 1 h (same as the e-mail).</summary>
public sealed record SmsRetryPolicy(int MaxAttempts, IReadOnlyList<TimeSpan> Delays)
{
    public static SmsRetryPolicy Default { get; } = new(5,
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)]);

    public TimeSpan DelayAfter(int attempts) => Delays[Math.Clamp(attempts - 1, 0, Delays.Count - 1)];
}

/// <summary>
/// HU-028: one short message of a CRITICAL alert waiting to leave (outbox, same pattern as the e-mail of HU-027). It is
/// written in the same flow that creates the notification, so a provider that is down never loses the alert. The phone
/// and the mode are NOT stored: they are asked to ms-iam when the message is sent (the user may have changed them).
/// </summary>
public sealed class SmsOutboxItem
{
    /// <summary>Two SMS at most; the composer keeps alerts within one (160 GSM-7 characters).</summary>
    public const int MaxBodyLength = 320;
    public const int MaxErrorLength = 500;
    public const int MaxProviderIdLength = 64;

    public Guid Id { get; }
    public Guid UserId { get; }
    public string? SourceEventId { get; }
    public string Body { get; }
    public SmsStatus Status { get; private set; }
    public ShortMessageChannel? Channel { get; private set; }
    public string? ProviderMessageId { get; private set; }
    public int Attempts { get; private set; }
    public DateTime NextAttemptAt { get; private set; }
    public string? LastError { get; private set; }
    public DateTime CreatedAt { get; }
    public DateTime? SentAt { get; private set; }

    private SmsOutboxItem(Guid id, Guid userId, string? sourceEventId, string body, SmsStatus status, ShortMessageChannel? channel,
        string? providerMessageId, int attempts, DateTime nextAttemptAt, string? lastError, DateTime createdAt, DateTime? sentAt)
    {
        Id = id;
        UserId = userId;
        SourceEventId = sourceEventId;
        Body = body;
        Status = status;
        Channel = channel;
        ProviderMessageId = providerMessageId;
        Attempts = attempts;
        NextAttemptAt = nextAttemptAt;
        LastError = lastError;
        CreatedAt = createdAt;
        SentAt = sentAt;
    }

    public static SmsOutboxItem Queue(Guid userId, string? sourceEventId, string body, DateTime now)
    {
        if (userId == Guid.Empty) throw new InvalidShortMessageException("The user is required.");
        if (string.IsNullOrWhiteSpace(body) || body.Length > MaxBodyLength)
            throw new InvalidShortMessageException($"The message must have 1 to {MaxBodyLength} characters.");
        return new(Guid.NewGuid(), userId, string.IsNullOrWhiteSpace(sourceEventId) ? null : sourceEventId.Trim(), body,
            SmsStatus.Pending, null, null, 0, now, null, now, null);
    }

    public static SmsOutboxItem Restore(Guid id, Guid userId, string? sourceEventId, string body, SmsStatus status,
        ShortMessageChannel? channel, string? providerMessageId, int attempts, DateTime nextAttemptAt, string? lastError,
        DateTime createdAt, DateTime? sentAt) =>
        new(id, userId, sourceEventId, body, status, channel, providerMessageId, attempts, nextAttemptAt, lastError, createdAt, sentAt);

    public bool IsDue(DateTime now) => Status == SmsStatus.Pending && now >= NextAttemptAt;

    /// <summary>The provider accepted it (Twilio answers its message SID).</summary>
    public void MarkSent(ShortMessageChannel channel, string? providerMessageId, DateTime now)
    {
        if (Status != SmsStatus.Pending) return;
        Status = SmsStatus.Sent;
        Channel = channel;
        ProviderMessageId = Trim(providerMessageId, MaxProviderIdLength);
        SentAt = now;
        Attempts++;
        LastError = null;
    }

    /// <summary>The user has no verified phone right now: there is nobody to write to, retrying will not help.</summary>
    public void Skip(string reason)
    {
        if (Status != SmsStatus.Pending) return;
        Status = SmsStatus.Skipped;
        LastError = Trim(reason, MaxErrorLength);
    }

    /// <summary>The provider refused the number for good (not a mobile, not on WhatsApp...): FAILED without retrying.</summary>
    public void MarkRejected(ShortMessageChannel channel, string reason)
    {
        if (Status != SmsStatus.Pending) return;
        Status = SmsStatus.Failed;
        Channel = channel;
        Attempts++;
        LastError = Trim(reason, MaxErrorLength);
    }

    /// <summary>The provider (or ms-iam) failed for now: try again later, or give up after the last attempt.</summary>
    public void RegisterFailure(string error, DateTime now, SmsRetryPolicy policy)
    {
        if (Status != SmsStatus.Pending) return;
        Attempts++;
        LastError = Trim(error, MaxErrorLength);
        if (Attempts >= policy.MaxAttempts) Status = SmsStatus.Failed;
        else NextAttemptAt = now + policy.DelayAfter(Attempts);
    }

    private static string? Trim(string? text, int max) =>
        string.IsNullOrEmpty(text) ? text : text.Length <= max ? text : text[..max];
}
