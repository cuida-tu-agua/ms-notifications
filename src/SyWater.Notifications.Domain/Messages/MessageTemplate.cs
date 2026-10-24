using System.Text.RegularExpressions;
using SyWater.Notifications.Domain.Common;

namespace SyWater.Notifications.Domain.Messages;

/// <summary>
/// The WhatsApp / SMS texts the admin can rewrite. Each one has a default text (the one the app shipped with) and the
/// placeholders it may use, e.g. "{place}" or "{code}".
/// </summary>
public enum MessageTemplateKey
{
    /// <summary>A CRITICAL alert by SMS / WhatsApp (HU-028).</summary>
    AlertCritical,

    /// <summary>The code that verifies the phone (HU-028).</summary>
    PhoneCode,

    /// <summary>A registered user is invited to collaborate in a place (HU-042).</summary>
    CollaboratorInvitation,

    /// <summary>The owner shares the guest link from their own WhatsApp (HU-039).</summary>
    GuestLinkShare,
}

public static class MessageTemplateKeys
{
    /// <summary>The stable string of the database and the API.</summary>
    public static string ToCode(this MessageTemplateKey key) => key switch
    {
        MessageTemplateKey.AlertCritical => "ALERT_CRITICAL",
        MessageTemplateKey.PhoneCode => "PHONE_CODE",
        MessageTemplateKey.CollaboratorInvitation => "COLLABORATOR_INVITATION",
        _ => "GUEST_LINK_SHARE",
    };

    /// <summary>"ALERT_CRITICAL" / "alert-critical" / "alert_critical" → enum; anything else → null.</summary>
    public static MessageTemplateKey? Parse(string? value) =>
        value?.Trim().Replace('-', '_').ToUpperInvariant() switch
        {
            "ALERT_CRITICAL" => MessageTemplateKey.AlertCritical,
            "PHONE_CODE" => MessageTemplateKey.PhoneCode,
            "COLLABORATOR_INVITATION" => MessageTemplateKey.CollaboratorInvitation,
            "GUEST_LINK_SHARE" => MessageTemplateKey.GuestLinkShare,
            _ => null,
        };
}

/// <summary>A placeholder the text may use. A required one cannot be removed (a code message without the code is useless).</summary>
public sealed record MessagePlaceholder(string Name, bool Required = false)
{
    public string Token => $"{{{Name}}}";
}

/// <summary>What a template is for, its default text and the placeholders it accepts.</summary>
public sealed record MessageTemplateDefinition(MessageTemplateKey Key, string DefaultBody, IReadOnlyList<MessagePlaceholder> Placeholders);

public static class MessageTemplateCatalog
{
    /// <summary>The brand, always available as {app}.</summary>
    public const string Brand = "Cuida Tu Agua";

    private static readonly MessagePlaceholder App = new("app");

    public static IReadOnlyList<MessageTemplateDefinition> All { get; } =
    [
        new(MessageTemplateKey.AlertCritical,
            "{app} - ALERTA: {title} en {place}. Abre la app para revisarla.",
            [App, new("title"), new("place")]),
        new(MessageTemplateKey.PhoneCode,
            "{app}: tu código de verificación es {code}. Vence en {minutes} minutos. No lo compartas con nadie.",
            [App, new("code", Required: true), new("minutes")]),
        new(MessageTemplateKey.CollaboratorInvitation,
            "{app}: {inviter} te invitó a colaborar en \"{place}\". Podrás {permissions}. Entra a la app y acéptala en Invitaciones antes del {expires}.",
            [App, new("inviter"), new("place"), new("permissions"), new("expires")]),
        new(MessageTemplateKey.GuestLinkShare,
            "Hola, te comparto el consumo de agua de \"{place}\" en {app}. Puedes verlo sin registrarte (solo lectura): {link}",
            [App, new("place"), new("owner"), new("link", Required: true)]),
    ];

    public static MessageTemplateDefinition For(MessageTemplateKey key) => All.First(d => d.Key == key);
}

/// <summary>
/// The text a message of <see cref="Key"/> is written with: the default one, or the one an admin saved. The placeholders
/// are checked when it is saved, so rendering never fails.
/// </summary>
public sealed partial class MessageTemplate
{
    /// <summary>Same limit as the body of the sms_outbox (two SMS at most).</summary>
    public const int MaxBodyLength = 320;

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex PlaceholderPattern();

    public MessageTemplateKey Key { get; }
    public string Body { get; }
    /// <summary>False = the default text (nothing stored).</summary>
    public bool IsCustom { get; }
    public Guid? UpdatedBy { get; }
    public DateTime? UpdatedAt { get; }

    public MessageTemplateDefinition Definition => MessageTemplateCatalog.For(Key);

    private MessageTemplate(MessageTemplateKey key, string body, bool isCustom, Guid? updatedBy, DateTime? updatedAt)
    {
        Key = key;
        Body = body;
        IsCustom = isCustom;
        UpdatedBy = updatedBy;
        UpdatedAt = updatedAt;
    }

    public static MessageTemplate Default(MessageTemplateKey key) => new(key, MessageTemplateCatalog.For(key).DefaultBody, false, null, null);

    /// <summary>An admin rewrites the text. Every "{...}" must be a placeholder of this message and the required ones must stay.</summary>
    /// <exception cref="InvalidMessageTemplateException">Empty, too long, an unknown placeholder or a required one missing.</exception>
    public static MessageTemplate Custom(MessageTemplateKey key, string? body, Guid updatedBy, DateTime now)
    {
        var text = (body ?? "").Replace("\r\n", "\n").Trim();
        if (text.Length == 0) throw new InvalidMessageTemplateException("The text is required.");
        if (text.Length > MaxBodyLength) throw new InvalidMessageTemplateException($"The text has at most {MaxBodyLength} characters.");
        if (text.Count(c => c == '{') != text.Count(c => c == '}'))
            throw new InvalidMessageTemplateException("Every '{' must be closed with '}' (placeholders look like {place}).");

        var definition = MessageTemplateCatalog.For(key);
        var allowed = definition.Placeholders.Select(p => p.Name).ToHashSet();
        foreach (Match match in PlaceholderPattern().Matches(text))
        {
            if (!allowed.Contains(match.Groups[1].Value))
                throw new InvalidMessageTemplateException(
                    $"{match.Value} is not a placeholder of this message. Use: {string.Join(", ", definition.Placeholders.Select(p => p.Token))}.");
        }

        var missing = definition.Placeholders.Where(p => p.Required && !text.Contains(p.Token)).Select(p => p.Token).ToList();
        if (missing.Count > 0)
            throw new InvalidMessageTemplateException($"The text must keep {string.Join(", ", missing)}.");

        return new(key, text, true, updatedBy, now);
    }

    public static MessageTemplate Restore(MessageTemplateKey key, string body, Guid? updatedBy, DateTime updatedAt) =>
        new(key, body, true, updatedBy, updatedAt);

    /// <summary>
    /// The finished text. {app} is always the brand; a value that is missing leaves its placeholder empty. Cut at a word
    /// boundary when it would not fit in <paramref name="max"/> characters.
    /// </summary>
    public string Render(IReadOnlyDictionary<string, string?> values, int max = MaxBodyLength)
    {
        var text = PlaceholderPattern().Replace(Body, match =>
        {
            var name = match.Groups[1].Value;
            if (name == "app") return MessageTemplateCatalog.Brand;
            return values.TryGetValue(name, out var value) && value is not null ? OneLine(value) : "";
        });
        return Fit(text.Trim(), max);
    }

    // A value comes from users (a place name, a person): it must not break the message into lines
    private static string OneLine(string value) => Regex.Replace(value, @"[\p{Cc}]+", " ").Trim();

    private static string Fit(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text[..(max - 3)];
        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut).TrimEnd(' ', '.', ',', ':') + "...";
    }
}

/// <summary>The admin saved a text that cannot be used. HTTP 400.</summary>
public sealed class InvalidMessageTemplateException(string reason) : DomainException("message_template.invalid", reason);

/// <summary>There is no message with that key. HTTP 404.</summary>
public sealed class MessageTemplateNotFoundException(string key)
    : DomainException("message_template.not_found", $"There is no message '{key}'.");
