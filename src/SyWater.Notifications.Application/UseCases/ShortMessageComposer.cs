using System.Globalization;
using SyWater.Notifications.Domain.Messages;
using SyWater.Notifications.Domain.Notifications;
using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Application.UseCases;

/// <summary>
/// HU-028: "the message is short and says the place and the kind of emergency". The words come from the text the admin
/// can edit (<see cref="MessageTemplate"/>); the default ones fit in one SMS (160 GSM-7 characters) once the adapter drops
/// the accents, a custom text may use up to two. WhatsApp gets the same text with its accents.
/// </summary>
public static class ShortMessageComposer
{
    public const string Brand = MessageTemplateCatalog.Brand;

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-CO");
    private static readonly TimeZoneInfo Bogota = FindBogota();

    /// <summary>"Cuida Tu Agua - ALERTA: Válvula cerrada en Casa centro. Abre la app para revisarla."</summary>
    public static string ForAlert(Notification n, DeliveryContext context, MessageTemplate? template = null)
    {
        template ??= MessageTemplate.Default(MessageTemplateKey.AlertCritical);
        var place = string.IsNullOrWhiteSpace(context.PlaceLabel) ? "tu lugar" : context.PlaceLabel.Trim();
        return template.Render(new Dictionary<string, string?>
        {
            ["title"] = n.Title.Trim().TrimEnd('.'),
            ["place"] = place,
        }, MaxLength(template));
    }

    /// <summary>"Cuida Tu Agua: tu código de verificación es 123456. Vence en 5 minutos. No lo compartas con nadie."</summary>
    public static string ForVerificationCode(string code, int validMinutes, MessageTemplate? template = null)
    {
        template ??= MessageTemplate.Default(MessageTemplateKey.PhoneCode);
        return template.Render(new Dictionary<string, string?>
        {
            ["code"] = code,
            ["minutes"] = validMinutes.ToString(CultureInfo.InvariantCulture),
        }, MaxLength(template));
    }

    /// <summary>"Cuida Tu Agua: Ana te invitó a colaborar en "Casa centro". Podrás ver el consumo de agua. ..."</summary>
    public static string ForInvitation(string placeName, string? inviterName, IReadOnlyCollection<string> permissions,
        DateTime expiresAt, MessageTemplate? template = null)
    {
        template ??= MessageTemplate.Default(MessageTemplateKey.CollaboratorInvitation);
        return template.Render(new Dictionary<string, string?>
        {
            ["inviter"] = string.IsNullOrWhiteSpace(inviterName) ? "Alguien" : inviterName.Trim(),
            ["place"] = placeName.Trim(),
            ["permissions"] = DescribePermissions(permissions),
            ["expires"] = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc), Bogota)
                .ToString("d 'de' MMMM", Spanish),
        }, SmsOutboxItem.MaxBodyLength);
    }

    /// <summary>The permission keys of ms-places in words ("ver el consumo de agua y abrir y cerrar la válvula").</summary>
    public static string DescribePermissions(IReadOnlyCollection<string> permissions)
    {
        var words = permissions.Select(p => p.Trim().ToUpperInvariant() switch
        {
            "VIEW_CONSUMPTION" => "ver el consumo de agua",
            "VIEW_DEVICE_STATUS" => "ver el estado del medidor",
            "CONTROL_VALVE" => "abrir y cerrar la válvula",
            "VIEW_ALERTS" => "recibir las alertas",
            "VIEW_BILLS" => "ver las tarifas y el costo",
            var other => other.ToLowerInvariant().Replace('_', ' '),
        }).Where(w => w.Length > 0).Distinct().ToList();

        return words.Count switch
        {
            0 => "ver el lugar",
            1 => words[0],
            _ => string.Join(", ", words[..^1]) + " y " + words[^1],
        };
    }

    // The default texts were written for one SMS; a custom one may take two (the admin sees the counter)
    private static int MaxLength(MessageTemplate template) =>
        template.IsCustom ? SmsOutboxItem.MaxBodyLength : GsmText.SegmentLength;

    private static TimeZoneInfo FindBogota()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("America/Bogota", TimeSpan.FromHours(-5), "Bogota", "Bogota");
        }
    }
}
