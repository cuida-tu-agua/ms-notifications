using SyWater.Notifications.Domain.Notifications;
using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Application.UseCases;

/// <summary>
/// HU-028: "the message is short and says the place and the kind of emergency". One SMS (160 GSM-7 characters) once the
/// adapter drops the accents; WhatsApp gets the same text with its accents.
/// </summary>
public static class ShortMessageComposer
{
    public const string Brand = "Cuida Tu Agua";

    /// <summary>"Cuida Tu Agua - ALERTA: Válvula cerrada en Casa centro. Abre la app para revisarla."</summary>
    public static string ForAlert(Notification n, DeliveryContext context)
    {
        var place = string.IsNullOrWhiteSpace(context.PlaceLabel) ? "tu lugar" : context.PlaceLabel.Trim();
        // Dropping an accent keeps the length, so fitting the original text also fits its SMS version
        return GsmText.Fit($"{Brand} - ALERTA: {n.Title.Trim().TrimEnd('.')} en {place}. Abre la app para revisarla.");
    }

    /// <summary>"Cuida Tu Agua: tu código de verificación es 123456. Vence en 5 minutos. No lo compartas."</summary>
    public static string ForVerificationCode(string code, int validMinutes) =>
        $"{Brand}: tu código de verificación es {code}. Vence en {validMinutes} minutos. No lo compartas con nadie.";
}
