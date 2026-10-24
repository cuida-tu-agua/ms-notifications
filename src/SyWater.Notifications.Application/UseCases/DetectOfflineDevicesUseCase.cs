using SyWater.Notifications.Application.Ports.In;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.Notifications;

namespace SyWater.Notifications.Application.UseCases;

/// <summary>
/// HU-032: a device that reported before and has been silent for the threshold (default 10 min) → ONE "sensor
/// disconnected" alert (important: in-app + push by default) that says when it was last heard. No more alerts
/// until it reports again.
/// </summary>
public sealed class DetectOfflineDevicesUseCase(
    IPlaceDeviceRepository devices, NotificationDispatcher dispatcher, OfflineSettings settings, TimeProvider clock,
    IPlaceAlertRecipients? recipients = null)
    : IDetectOfflineDevicesUseCase
{
    public const int BatchSize = 50;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var created = 0;
        foreach (var device in await devices.FindSilentAsync(now - settings.After, BatchSize, ct))
        {
            if (!device.IsSilent(now, settings.After)) continue;
            var lastSeen = device.LastReadingAt!.Value;
            var minutes = (int)(now - lastSeen).TotalMinutes;

            // Idempotent by (device, last reading): if we crash before marking it, the next sweep notifies nothing twice.
            var notification = Notification.Create(
                Audience.ForUser(device.UserId), NotificationType.DeviceOffline, NotificationSeverity.Warning,
                "Sensor desconectado",
                $"Tu dispositivo {device.SerialNumber} no reporta desde {lastSeen:yyyy-MM-dd HH:mm} UTC (hace {minutes} min). " +
                "Revisa que tenga energía y conexión WiFi.",
                device.PlaceId, $"offline:{device.DeviceId:N}:{lastSeen:yyyyMMddHHmmss}", now);
            var context = new DeliveryContext($"Lugar con el dispositivo {device.SerialNumber}");
            await dispatcher.DispatchAsync(notification, context, ct);

            // HU-047: the collaborators with VIEW_ALERTS hear about it too. If ms-places is down the device is NOT marked
            // as alerted, so the next sweep tries again (the owner's notification is idempotent, nobody gets it twice).
            if (recipients is not null)
            {
                try
                {
                    foreach (var collaboratorId in await recipients.CollaboratorsWithAlertsAsync(device.PlaceId, ct))
                    {
                        if (collaboratorId == device.UserId) continue;
                        await dispatcher.DispatchAsync(
                            Notification.Create(Audience.ForUser(collaboratorId), NotificationType.DeviceOffline, NotificationSeverity.Warning,
                                "Sensor desconectado",
                                $"El medidor de un lugar compartido contigo no reporta desde {lastSeen:yyyy-MM-dd HH:mm} UTC (hace {minutes} min).",
                                device.PlaceId, notification.SourceEventId, now),
                            context, ct);
                    }
                }
                catch (ExternalServiceUnavailableException)
                {
                    continue;
                }
            }

            if (await devices.MarkOfflineAlertedAsync(device, now, ct)) created++;
        }
        return created;
    }
}
