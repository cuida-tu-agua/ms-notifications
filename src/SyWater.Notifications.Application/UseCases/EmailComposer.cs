using System.Net;
using SyWater.Notifications.Domain.Notifications;

namespace SyWater.Notifications.Application.UseCases;

/// <summary>What the e-mail says besides the notification itself.</summary>
public sealed record DeliveryContext(string? PlaceLabel = null);

/// <summary>
/// HU-027: the mail says the place, the kind of alert, the date/time and what to do, with a direct link to the app.
/// </summary>
public static class EmailComposer
{
    public static (string Subject, string Text, string Html) Compose(Notification n, DeliveryContext context, string appBaseUrl)
    {
        var level = n.Severity switch
        {
            NotificationSeverity.Critical => "ALERTA CRÍTICA",
            NotificationSeverity.Warning => "Aviso importante",
            _ => "Aviso",
        };
        var kind = n.Type switch
        {
            NotificationType.ValveChanged => "Estado de la válvula",
            NotificationType.DeviceOffline => "Sensor desconectado",
            NotificationType.DeviceLinked => "Dispositivo vinculado",
            NotificationType.DeviceUnlinked => "Dispositivo desvinculado",
            _ => "Anuncio",
        };
        var place = string.IsNullOrWhiteSpace(context.PlaceLabel) ? "Tu lugar" : context.PlaceLabel.Trim();
        var when = LocalTime.Format(n.CreatedAt);
        var link = Link(appBaseUrl, n.PlaceId);

        var subject = $"[Cuida Tu Agua] {level}: {n.Title}";
        var text = $"""
            {level}: {n.Title}

            Lugar: {place}
            Tipo de alerta: {kind}
            Fecha y hora: {when}

            Qué hacer: {n.Body}

            Abrir en la app: {link}

            — Cuida Tu Agua. Puedes elegir qué avisos recibes por correo en las preferencias de notificación.
            """;
        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:560px;margin:auto">
              <h2 style="margin:0 0 12px">{Enc(level)}: {Enc(n.Title)}</h2>
              <table style="border-collapse:collapse">
                <tr><td style="padding:2px 12px 2px 0"><b>Lugar</b></td><td>{Enc(place)}</td></tr>
                <tr><td style="padding:2px 12px 2px 0"><b>Tipo de alerta</b></td><td>{Enc(kind)}</td></tr>
                <tr><td style="padding:2px 12px 2px 0"><b>Fecha y hora</b></td><td>{Enc(when)}</td></tr>
              </table>
              <p><b>Qué hacer:</b> {Enc(n.Body)}</p>
              <p><a href="{Enc(link)}" style="background:#0b7285;color:#fff;padding:10px 16px;border-radius:6px;text-decoration:none">Abrir en la app</a></p>
              <p style="color:#666;font-size:12px">Cuida Tu Agua. Puedes elegir qué avisos recibes por correo en las preferencias de notificación.</p>
            </div>
            """;
        return (subject, text, html);
    }

    private static string Link(string appBaseUrl, Guid? placeId)
    {
        var baseUrl = appBaseUrl.TrimEnd('/');
        return placeId is { } id ? $"{baseUrl}/places/{id}" : baseUrl;
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
