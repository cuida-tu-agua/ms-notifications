using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Application.UseCases;
using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Application.Tests;

public sealed class FakeStatuses : IShortMessageStatusReader
{
    public Queue<ShortMessageStatus?> Answers { get; } = new();
    public bool Down { get; set; }
    public int Reads { get; private set; }

    public Task<ShortMessageStatus?> ReadAsync(string providerMessageId, CancellationToken ct)
    {
        Reads++;
        if (Down) throw new ShortMessageSendException("Twilio 503");
        return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : null);
    }
}

public class DeliveryStatusTests
{
    private static readonly TestStatusPolling Fast = new(3, TimeSpan.Zero);
    private readonly FakeShortMessageSender _sender = new();
    private readonly FakeContacts _contacts = new();
    private readonly FakeStatuses _statuses = new();
    private readonly Guid _admin = Guid.NewGuid();

    public DeliveryStatusTests() =>
        _contacts.Known[_admin] = new UserContact(_admin, "admin@mail.com", "Ada", "+573001112233", true, ShortMessageChannel.WhatsApp);

    private SendTestMessageUseCase UseCase() =>
        new(_sender, new SmsSettings(true), MessageTexts.Defaults, _contacts, _statuses, Fast);

    [Fact]
    public async Task A_whatsapp_that_Twilio_rejects_later_is_shown_as_rejected_with_the_reason()
    {
        _statuses.Answers.Enqueue(ShortMessageStatus.From("queued", null));
        _statuses.Answers.Enqueue(ShortMessageStatus.From("undelivered", "Twilio 63016: outside the allowed window"));

        var result = await UseCase().ExecuteAsync(_admin, "ALERT_CRITICAL", default);

        Assert.False(result.Sent);
        Assert.Equal("rejected", result.Problem);
        Assert.Contains("63016", result.Detail);
        Assert.Equal("undelivered", result.DeliveryStatus);
    }

    [Fact]
    public async Task A_delivered_message_stops_waiting_and_says_so()
    {
        _statuses.Answers.Enqueue(ShortMessageStatus.From("delivered", null));

        var result = await UseCase().ExecuteAsync(_admin, "ALERT_CRITICAL", default);

        Assert.True(result.Sent);
        Assert.Equal("delivered", result.DeliveryStatus);
        Assert.Equal(1, _statuses.Reads);
    }

    [Fact]
    public async Task If_the_status_cannot_be_read_the_message_still_counts_as_sent()
    {
        _statuses.Down = true;

        var result = await UseCase().ExecuteAsync(_admin, "ALERT_CRITICAL", default);

        Assert.True(result.Sent);
        Assert.Null(result.DeliveryStatus);
        Assert.Equal(3, _statuses.Reads);
    }

    [Fact]
    public void The_app_only_offers_the_modes_that_can_arrive()
    {
        Assert.Equal(["WHATSAPP"], new GetShortMessageChannelsUseCase(new ShortMessageProviders("", "twilio", true)).Execute().Channels);
        Assert.Equal(["WHATSAPP", "SMS"], new GetShortMessageChannelsUseCase(new ShortMessageProviders("mailpit", "mailpit", false)).Execute().Channels);
        Assert.Empty(new GetShortMessageChannelsUseCase(new ShortMessageProviders("", "", false)).Execute().Channels);
    }
}
