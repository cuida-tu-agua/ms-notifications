using SyWater.Notifications.Application.Ports.In;

namespace SyWater.Notifications.Api.Background;

/// <summary>
/// HU-032 "sensor desconectado": every 15 s, the devices that reported before and stayed silent longer than the
/// threshold (default 1 min) get ONE alert.
/// </summary>
public sealed class OfflineDeviceWorker(IServiceScopeFactory scopes, ILogger<OfflineDeviceWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var count = await scope.ServiceProvider.GetRequiredService<IDetectOfflineDevicesUseCase>().ExecuteAsync(stoppingToken);
                if (count > 0) logger.LogInformation("{Count} device(s) went silent: owners notified", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Offline-device sweep failed; retrying in {Seconds}s", Every.TotalSeconds);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
