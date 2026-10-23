using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.ShortMessages;
using SyWater.Notifications.Infrastructure.Http;
using SyWater.Notifications.Infrastructure.Persistence;
using SyWater.Notifications.Infrastructure.ShortMessages;

namespace SyWater.Notifications.Infrastructure.Tests;

public sealed class EfSmsOutboxRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _sqlite = new("DataSource=:memory:");
    private readonly DbContextOptions<NotificationDbContext> _options;
    private readonly Guid _user = Guid.NewGuid();

    public EfSmsOutboxRepositoryTests()
    {
        _sqlite.Open();
        _options = new DbContextOptionsBuilder<NotificationDbContext>().UseSqlite(_sqlite).Options;
        using var db = new NotificationDbContext(_options);
        db.Database.EnsureCreated();
    }

    private EfSmsOutboxRepository Repo() => new(new NotificationDbContext(_options));

    [Fact]
    public async Task The_same_source_event_queues_one_message_per_user()
    {
        Assert.True(await Repo().AddAsync(SmsOutboxItem.Queue(_user, "evt-1", "Hola", Now), default));
        Assert.False(await Repo().AddAsync(SmsOutboxItem.Queue(_user, "evt-1", "Hola", Now), default));
        Assert.True(await Repo().AddAsync(SmsOutboxItem.Queue(Guid.NewGuid(), "evt-1", "Hola", Now), default));
    }

    [Fact]
    public async Task Only_pending_due_messages_are_returned_and_the_outcome_keeps_mode_and_provider_id()
    {
        await Repo().AddAsync(SmsOutboxItem.Queue(_user, "evt-1", "Hola", Now), default);
        await Repo().AddAsync(SmsOutboxItem.Queue(_user, "evt-2", "Hola", Now.AddMinutes(10)), default);   // not due yet

        var due = Assert.Single(await Repo().GetDueAsync(Now.AddMinutes(1), 10, default));
        Assert.Equal(DateTimeKind.Utc, due.NextAttemptAt.Kind);

        due.MarkSent(ShortMessageChannel.WhatsApp, "SM123", Now.AddMinutes(1));
        Assert.True(await Repo().SaveOutcomeAsync(due, default));
        Assert.False(await Repo().SaveOutcomeAsync(due, default));   // already finished: a second worker cannot finish it again

        using var db = new NotificationDbContext(_options);
        var row = await db.SmsOutbox.SingleAsync(e => e.SourceEventId == "evt-1");
        Assert.Equal("SENT", row.Status);
        Assert.Equal("WHATSAPP", row.Channel);
        Assert.Equal("SM123", row.ProviderMessageId);
        Assert.Empty(await Repo().GetDueAsync(Now.AddMinutes(2), 10, default));
    }

    [Fact]
    public async Task A_skipped_message_is_stored_as_SKIPPED()
    {
        await Repo().AddAsync(SmsOutboxItem.Queue(_user, "evt-1", "Hola", Now), default);
        var due = Assert.Single(await Repo().GetDueAsync(Now, 10, default));
        due.Skip("The user has no verified phone.");
        await Repo().SaveOutcomeAsync(due, default);

        using var db = new NotificationDbContext(_options);
        Assert.Equal("SKIPPED", (await db.SmsOutbox.SingleAsync()).Status);
    }

    public void Dispose() => _sqlite.Dispose();
}

public class TwilioShortMessageSenderTests
{
    private sealed class Stub(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static TwilioShortMessageSender Sender(Stub stub, string smsFrom = "+15005550006", string whatsAppFrom = "+14155238886") =>
        new(new HttpClient(stub) { BaseAddress = new Uri("https://twilio.test/") },
            Options.Create(new TwilioOptions { AccountSid = "ACtest", AuthToken = "secret", SmsFrom = smsFrom, WhatsAppFrom = whatsAppFrom }));

    private static Dictionary<string, string> Form(string body) =>
        body.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    [Fact]
    public async Task An_sms_posts_to_the_account_with_basic_auth_and_text_without_accents()
    {
        var stub = new Stub(HttpStatusCode.Created, """{"sid":"SM42","status":"queued"}""");

        var id = await Sender(stub).SendAsync(new ShortMessage("+573001112233", ShortMessageChannel.Sms, "Válvula cerrada en Piña"), default);

        Assert.Equal("SM42", id);
        Assert.Equal(HttpMethod.Post, stub.Last!.Method);
        Assert.Equal("https://twilio.test/2010-04-01/Accounts/ACtest/Messages.json", stub.Last.RequestUri!.ToString());
        Assert.Equal("Basic", stub.Last.Headers.Authorization!.Scheme);
        Assert.Equal("ACtest:secret", Encoding.ASCII.GetString(Convert.FromBase64String(stub.Last.Headers.Authorization.Parameter!)));
        var form = Form(stub.Body);
        Assert.Equal("+573001112233", form["To"]);
        Assert.Equal("+15005550006", form["From"]);
        Assert.Equal("Valvula cerrada en Piña", form["Body"]);
    }

    [Fact]
    public async Task Whatsapp_uses_the_whatsapp_addresses_and_keeps_the_accents()
    {
        var stub = new Stub(HttpStatusCode.Created, """{"sid":"SM43"}""");

        await Sender(stub).SendAsync(new ShortMessage("+593991112233", ShortMessageChannel.WhatsApp, "Válvula cerrada"), default);

        var form = Form(stub.Body);
        Assert.Equal("whatsapp:+593991112233", form["To"]);
        Assert.Equal("whatsapp:+14155238886", form["From"]);
        Assert.Equal("Válvula cerrada", form["Body"]);
    }

    [Fact]
    public async Task A_number_twilio_refuses_is_a_rejection_with_its_code()
    {
        var stub = new Stub(HttpStatusCode.BadRequest, """{"code":21614,"message":"'To' number is not a valid mobile number"}""");

        var error = await Assert.ThrowsAsync<ShortMessageRejectedException>(() =>
            Sender(stub).SendAsync(new ShortMessage("+573001112233", ShortMessageChannel.Sms, "Hola"), default));

        Assert.Equal("21614", error.ProviderCode);
        Assert.Contains("not a valid mobile number", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]   // our credentials: retry after fixing them, never blame the user's number
    public async Task Server_errors_limits_and_bad_credentials_are_retried(HttpStatusCode status)
    {
        var stub = new Stub(status, """{"code":20003,"message":"x"}""");

        await Assert.ThrowsAsync<ShortMessageSendException>(() =>
            Sender(stub).SendAsync(new ShortMessage("+573001112233", ShortMessageChannel.Sms, "Hola"), default));
    }

    [Fact]
    public async Task Without_a_whatsapp_sender_configured_whatsapp_is_rejected_without_calling_twilio()
    {
        var stub = new Stub(HttpStatusCode.Created, "{}");

        await Assert.ThrowsAsync<ShortMessageRejectedException>(() =>
            Sender(stub, whatsAppFrom: "").SendAsync(new ShortMessage("+573001112233", ShortMessageChannel.WhatsApp, "Hola"), default));
        Assert.Null(stub.Last);
    }
}

public class MailpitShortMessageSenderTests
{
    private sealed class FakeEmail : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public bool Down { get; set; }

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            if (Down) throw new EmailSendException("SMTP down");
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Each_fake_sms_is_a_mail_by_number_and_mode_with_the_text_the_phone_would_show()
    {
        var email = new FakeEmail();
        var sender = new MailpitShortMessageSender(email);

        var id = await sender.SendAsync(new ShortMessage("+573001112233", ShortMessageChannel.Sms, "Válvula cerrada"), default);
        await sender.SendAsync(new ShortMessage("+593991112233", ShortMessageChannel.WhatsApp, "Válvula cerrada"), default);

        Assert.StartsWith("mailpit-", id);
        Assert.Equal("sms.573001112233@sms.cuidatuagua.local", email.Sent[0].ToAddress);
        Assert.Equal("[SMS] +573001112233", email.Sent[0].Subject);
        Assert.StartsWith("Valvula cerrada", email.Sent[0].TextBody);
        Assert.Equal("whatsapp.593991112233@sms.cuidatuagua.local", email.Sent[1].ToAddress);
        Assert.StartsWith("Válvula cerrada", email.Sent[1].TextBody);
    }

    [Fact]
    public async Task Mailpit_down_is_retried_like_a_provider_down()
    {
        var sender = new MailpitShortMessageSender(new FakeEmail { Down = true });

        await Assert.ThrowsAsync<ShortMessageSendException>(() =>
            sender.SendAsync(new ShortMessage("+573001112233", ShortMessageChannel.Sms, "Hola"), default));
    }
}

public class ContactPhoneTests
{
    private sealed class Stub(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private static Task<UserContact?> Find(string json) =>
        new HttpUserContactDirectory(new HttpClient(new Stub(json)) { BaseAddress = new Uri("http://iam.test/") }, "key")
            .FindAsync(Guid.NewGuid(), default);

    [Fact]
    public async Task The_verified_phone_and_its_mode_come_from_ms_iam()
    {
        var contact = await Find("""{"email":"a@b.co","fullName":"Ana","phone":"+573001112233","phoneVerified":true,"phoneChannel":"WHATSAPP"}""");

        Assert.Equal(("+573001112233", ShortMessageChannel.WhatsApp), contact!.VerifiedPhone);
    }

    [Theory]
    [InlineData("""{"email":"a@b.co"}""")]                                                                       // an ms-iam without HU-028
    [InlineData("""{"email":"a@b.co","phone":"+573001112233","phoneVerified":false,"phoneChannel":"SMS"}""")]   // not verified
    [InlineData("""{"email":"a@b.co","phone":"+573001112233","phoneVerified":true,"phoneChannel":null}""")]      // no mode
    [InlineData("""{"email":"a@b.co","phone":"3001112233","phoneVerified":true,"phoneChannel":"SMS"}""")]        // not E.164
    public async Task Anything_less_than_a_verified_E164_phone_with_its_mode_means_no_phone(string json)
    {
        var contact = await Find(json);

        Assert.NotNull(contact);   // the e-mail still works
        Assert.Null(contact!.VerifiedPhone);
    }
}
