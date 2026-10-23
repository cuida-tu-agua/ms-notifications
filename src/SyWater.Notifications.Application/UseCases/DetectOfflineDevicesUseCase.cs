using SyWater.Notifications.Application.Ports.In;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.Notifications;

namespace SyWater.Notifications.Application.UseCases;

/// <summary>
/// HU-032: a device that reported before and has been silent for the threshold (default 1 min) → ONE "sensor
/// disconnected" alert (important: in-app + push by default) that says when it was last heard. No more alerts
/// until it reports again.
/// </summary>
public sealed class DetectOfflineDevicesUseCase(
    IPlaceDeviceRepository devices, NotificationDispatcher dispatcher, OfflineSettings settings, TimeProvider clock)
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
                $"Tu dispositivo {device.SerialNumber} no reporta desde {LocalTime.Format(lastSeen)} (hace {minutes} min). " +
                "Revisa que tenga energía y conexión WiFi.",
                device.PlaceId, $"offline:{device.DeviceId:N}:{lastSeen:yyyyMMddHHmmss}", now);
            await dispatcher.DispatchAsync(notification, new DeliveryContext($"Lugar con el dispositivo {device.SerialNumber}"), ct);

            if (await devices.MarkOfflineAlertedAsync(device, now, ct)) created++;
        }
        return created;
    }
}
