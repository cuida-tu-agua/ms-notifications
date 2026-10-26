using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.Messages;
using SyWater.Notifications.Domain.ShortMessages;
using SyWater.Notifications.Infrastructure.Persistence;
using SyWater.Notifications.Infrastructure.ShortMessages;

namespace SyWater.Notifications.Infrastructure.Tests;

public class MetaWhatsAppSenderTests
{
    private sealed class Stub(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public JsonElement Body { get; private set; }
        public bool Fail { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Fail) throw new HttpRequestException("no network");
            Last = request;
            Body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private const string Ok = """{"messaging_product":"whatsapp","contacts":[{"input":"573001112233","wa_id":"573001112233"}],"messages":[{"id":"wamid.HBgMNTczMDAxMTEyMjMzFQIAERgSQzQ"}]}""";

    private static (MetaWhatsAppSender Sender, Stub Stub) Sender(HttpStatusCode status = HttpStatusCode.OK, string json = Ok,
        string template = "", string codeTemplate = "", bool fail = false)
    {
        var stub = new Stub(status, json) { Fail = fail };
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://graph.facebook.com/") };
        var options = Options.Create(new MetaWhatsAppOptions
        {
            AccessToken = "EAAG-test", PhoneNumberId = "1234567890", TemplateName = template, CodeTemplateName = codeTemplate,
        });
        return (new MetaWhatsAppSender(http, options), stub);
    }

    private static ShortMessage Alert(string body = "Cuida Tu Agua - ALERTA: Válvula cerrada en Casa.") =>
        new("+573001112233", ShortMessageChannel.WhatsApp, body);

    [Fact]
    public async Task Without_a_template_it_sends_free_text_to_the_number_without_plus_with_the_bearer_token()
    {
        var (sender, stub) = Sender();

        var id = await sender.SendAsync(Alert(), default);

        Assert.Equal("wamid.HBgMNTczMDAxMTEyMjMzFQIAERgSQzQ", id);
        Assert.Equal("https://graph.facebook.com/v23.0/1234567890/messages", stub.Last!.RequestUri!.ToString());
        Assert.Equal("Bearer", stub.Last.Headers.Authorization!.Scheme);
        Assert.Equal("EAAG-test", stub.Last.Headers.Authorization.Parameter);
        Assert.Equal("573001112233", stub.Body.GetProperty("to").GetString());
        Assert.Equal("text", stub.Body.GetProperty("type").GetString());
        Assert.Equal("Cuida Tu Agua - ALERTA: Válvula cerrada en Casa.", stub.Body.GetProperty("text").GetProperty("body").GetString());
    }

    [Fact]
    public async Task With_a_template_the_whole_text_goes_in_its_only_variable_in_one_line()
    {
        var (sender, stub) = Sender(template: "alerta_cuida_tu_agua");

        await sender.SendAsync(Alert("Línea 1\nLínea    2"), default);

        var template = stub.Body.GetProperty("template");
        Assert.Equal("template", stub.Body.GetProperty("type").GetString());
        Assert.Equal("alerta_cuida_tu_agua", template.GetProperty("name").GetString());
        Assert.Equal("es", template.GetProperty("language").GetProperty("code").GetString());
        var parameter = template.GetProperty("components")[0].GetProperty("parameters")[0];
        Assert.Equal("Línea 1 Línea 2", parameter.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_code_goes_with_the_authentication_template_and_its_copy_button()
    {
        var (sender, stub) = Sender(template: "alerta_cuida_tu_agua", codeTemplate: "codigo_verificacion");

        await sender.SendAsync(new ShortMessage("+593991112233", ShortMessageChannel.WhatsApp, "Tu código es 123456",
            ShortMessageKind.VerificationCode, "123456"), default);

        var template = stub.Body.GetProperty("template");
        Assert.Equal("codigo_verificacion", template.GetProperty("name").GetString());
        var components = template.GetProperty("components");
        Assert.Equal("123456", components[0].GetProperty("parameters")[0].GetProperty("text").GetString());
        Assert.Equal("button", components[1].GetProperty("type").GetString());
        Assert.Equal("123456", components[1].GetProperty("parameters")[0].GetProperty("text").GetString());
        Assert.Equal("593991112233", stub.Body.GetProperty("to").GetString());
    }

    [Theory]
    [InlineData(131030, "Recipient phone number not in allowed list")]
    [InlineData(131026, "Message undeliverable")]
    [InlineData(131047, "Re-engagement message")]
    [InlineData(132001, "Template name does not exist in the translation")]
    public async Task What_Meta_refuses_for_good_is_rejected_with_its_code(int code, string text)
    {
        var json = JsonSerializer.Serialize(new { error = new { message = text, type = "OAuthException", code, error_data = new { details = "detail" } } });
        var (sender, _) = Sender(HttpStatusCode.BadRequest, json);

        var ex = await Assert.ThrowsAsync<ShortMessageRejectedException>(() => sender.SendAsync(Alert(), default));

        Assert.Equal(code.ToString(), ex.ProviderCode);
        Assert.Contains(text, ex.Message);
        Assert.Contains("detail", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"message":"Error validating access token","code":190}}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"Rate limit hit","code":130429}}""")]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":{"message":"Something went wrong","code":131000}}""")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "not json")]
    public async Task An_expired_token_a_rate_limit_or_a_failure_of_Meta_is_retried_later(HttpStatusCode status, string json)
    {
        var (sender, _) = Sender(status, json);

        await Assert.ThrowsAsync<ShortMessageSendException>(() => sender.SendAsync(Alert(), default));
    }

    [Fact]
    public async Task No_network_is_retried_later()
    {
        var (sender, _) = Sender(fail: true);

        await Assert.ThrowsAsync<ShortMessageSendException>(() => sender.SendAsync(Alert(), default));
    }

    [Fact]
    public async Task An_sms_never_goes_to_Meta()
    {
        var (sender, stub) = Sender();

        await Assert.ThrowsAsync<ShortMessageRejectedException>(() =>
            sender.SendAsync(new ShortMessage("+573001112233", ShortMessageChannel.Sms, "Hola"), default));
        Assert.Null(stub.Last);
    }
}

public class ChannelRoutingShortMessageSenderTests
{
    private sealed class Recorder(string id) : IShortMessageSender
    {
        public List<ShortMessage> Sent { get; } = [];

        public Task<string?> SendAsync(ShortMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult<string?>(id);
        }
    }

    [Fact]
    public async Task Each_mode_goes_by_its_own_provider()
    {
        var sms = new Recorder("SM1");
        var whatsApp = new Recorder("wamid.1");
        var router = new ChannelRoutingShortMessageSender(sms, whatsApp);

        Assert.Equal("wamid.1", await router.SendAsync(new ShortMessage("+573001112233", ShortMessageChannel.WhatsApp, "a"), default));
        Assert.Equal("SM1", await router.SendAsync(new ShortMessage("+573001112233", ShortMessageChannel.Sms, "b"), default));
        Assert.Single(sms.Sent);
        Assert.Single(whatsApp.Sent);
    }

    [Fact]
    public async Task A_mode_without_provider_is_refused_for_good()
    {
        var router = new ChannelRoutingShortMessageSender(null, new Recorder("wamid.1"));

        await Assert.ThrowsAsync<ShortMessageRejectedException>(() =>
            router.SendAsync(new ShortMessage("+573001112233", ShortMessageChannel.Sms, "b"), default));
    }
}

public sealed class EfMessageTemplateRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 24, 3, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _sqlite = new("DataSource=:memory:");
    private readonly DbContextOptions<NotificationDbContext> _options;

    public EfMessageTemplateRepositoryTests()
    {
        _sqlite.Open();
        _options = new DbContextOptionsBuilder<NotificationDbContext>().UseSqlite(_sqlite).Options;
        using var db = new NotificationDbContext(_options);
        db.Database.EnsureCreated();
    }

    private EfMessageTemplateRepository Repo() => new(new NotificationDbContext(_options));

    [Fact]
    public async Task A_saved_text_is_found_replaced_listed_and_deleted()
    {
        var admin = Guid.NewGuid();
        Assert.Null(await Repo().FindCustomAsync(MessageTemplateKey.AlertCritical, default));

        await Repo().SaveAsync(MessageTemplate.Custom(MessageTemplateKey.AlertCritical, "Alerta {title}", admin, Now), default);
        await Repo().SaveAsync(MessageTemplate.Custom(MessageTemplateKey.AlertCritical, "Alerta en {place}", admin, Now.AddMinutes(1)), default);

        var found = await Repo().FindCustomAsync(MessageTemplateKey.AlertCritical, default);
        Assert.NotNull(found);
        Assert.True(found.IsCustom);
        Assert.Equal("Alerta en {place}", found.Body);
        Assert.Equal(admin, found.UpdatedBy);
        Assert.Equal(DateTimeKind.Utc, found.UpdatedAt!.Value.Kind);
        Assert.Single(await Repo().ListCustomAsync(default));

        using (var db = new NotificationDbContext(_options))
            Assert.Equal("ALERT_CRITICAL", (await db.MessageTemplates.SingleAsync()).TemplateKey);

        Assert.True(await Repo().DeleteAsync(MessageTemplateKey.AlertCritical, default));
        Assert.False(await Repo().DeleteAsync(MessageTemplateKey.AlertCritical, default));
        Assert.Empty(await Repo().ListCustomAsync(default));
    }

    public void Dispose() => _sqlite.Dispose();
}
