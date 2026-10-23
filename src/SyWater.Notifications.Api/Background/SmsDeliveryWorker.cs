using SyWater.Notifications.Application.Ports.In;

namespace SyWater.Notifications.Api.Background;

/// <summary>
/// HU-028: every 10 s, sends the SMS / WhatsApp messages of the outbox that are due. A message that fails is retried
/// later with a growing delay (see SmsRetryPolicy); the worker itself never stops because of one bad message.
/// </summary>
public sealed class SmsDeliveryWorker(IServiceScopeFactory scopes, ILogger<SmsDeliveryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var sent = await scope.ServiceProvider.GetRequiredService<ISendDueShortMessagesUseCase>().ExecuteAsync(stoppingToken);
                if (sent > 0) logger.LogInformation("{Count} SMS / WhatsApp message(s) sent", sent);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SMS sweep failed; retrying in {Seconds}s", Every.TotalSeconds);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
