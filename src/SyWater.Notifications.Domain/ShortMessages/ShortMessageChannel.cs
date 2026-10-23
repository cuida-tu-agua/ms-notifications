using System.Text.RegularExpressions;
using SyWater.Notifications.Domain.Common;

namespace SyWater.Notifications.Domain.ShortMessages;

/// <summary>
/// HU-028: how a short message reaches the phone. The user picks it when verifying the phone (ms-iam keeps it in
/// security.users.phone_channel) and every critical alert uses that same mode afterwards.
/// </summary>
public enum ShortMessageChannel { Sms, WhatsApp }

public static class ShortMessageChannels
{
    /// <summary>"SMS" / "WHATSAPP" (any case) → enum; anything else → null.</summary>
    public static ShortMessageChannel? Parse(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "SMS" => ShortMessageChannel.Sms,
        "WHATSAPP" => ShortMessageChannel.WhatsApp,
        _ => null,
    };

    /// <summary>The stable string of the database and the APIs.</summary>
    public static string ToCode(this ShortMessageChannel channel) => channel == ShortMessageChannel.WhatsApp ? "WHATSAPP" : "SMS";
}

/// <summary>A phone in E.164 ("+573001112233"): what ms-iam stores once it is verified and what the providers expect.</summary>
public static partial class PhoneNumbers
{
    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$")]
    private static partial Regex E164();

    public static bool IsE164(string? phone) => phone is not null && E164().IsMatch(phone);

    /// <summary>"+573001112233" → "+57300***233": enough to recognize it in a log, not enough to read it.</summary>
    public static string Mask(string? phone) =>
        phone is null || phone.Length < 8 ? "***" : $"{phone[..6]}***{phone[^3..]}";
}

/// <summary>A request to send a short message that cannot be valid (bad phone, mode or code). HTTP 400.</summary>
public sealed class InvalidShortMessageException(string reason)
    : DomainException("sms.invalid_request", reason);
