using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Api.Tests;

public sealed class FakeShortMessages : IShortMessageSender
{
    public List<ShortMessage> Sent { get; } = [];
    public string? RejectWith { get; set; }
    public bool Down { get; set; }

    public Task<string?> SendAsync(ShortMessage message, CancellationToken ct)
    {
        if (Down) throw new ShortMessageSendException("Twilio 503");
        if (RejectWith is not null) throw new ShortMessageRejectedException(RejectWith, "63003");
        Sent.Add(message);
        return Task.FromResult<string?>("SM-api-1");
    }
}

public class ShortMessagesApiTests : IClassFixture<ApiFactory>
{
    private const string Key = "test-internal-key-0123456789";
    private const string Url = "/internal/short-messages/verification-code";
    private readonly ApiFactory _factory;

    public ShortMessagesApiTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.CreateSchema();
    }

    /// <summary>The same API with SMS / WhatsApp switched on and a fake provider.</summary>
    private (HttpClient Client, FakeShortMessages Sms) WithSms(string? key = Key)
    {
        var sms = new FakeShortMessages();
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Sms:Provider", "mailpit");
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IShortMessageSender>();
                s.AddSingleton<IShortMessageSender>(sms);
            });
        }).CreateClient();
        if (key is not null) client.DefaultRequestHeaders.Add("X-Internal-Key", key);
        return (client, sms);
    }

    private static object Request(string to = "+573001112233", string channel = "WHATSAPP", string code = "123456") =>
        new { to, channel, code, validMinutes = 5 };

    private static async Task<string?> Title(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

    [Fact]
    public async Task Ms_iam_sends_the_code_by_the_mode_the_user_picked()
    {
        var (client, sms) = WithSms();

        var response = await client.PostAsJsonAsync(Url, Request());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("SM-api-1", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("providerMessageId").GetString());
        var sent = Assert.Single(sms.Sent);
        Assert.Equal(ShortMessageChannel.WhatsApp, sent.Channel);
        Assert.Contains("123456", sent.Body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key-0123456789012345")]
    public async Task Without_the_internal_key_it_is_403_and_nothing_is_sent(string? key)
    {
        var (client, sms) = WithSms(key);

        var response = await client.PostAsJsonAsync(Url, Request());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.internal_only", await Title(response));
        Assert.Empty(sms.Sent);
    }

    [Fact]
    public async Task A_phone_without_country_code_is_400()
    {
        var (client, _) = WithSms();

        var response = await client.PostAsJsonAsync(Url, Request(to: "3001112233"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("sms.invalid_request", await Title(response));
    }

    [Fact]
    public async Task A_number_that_does_not_work_for_the_mode_is_422_so_the_user_can_fix_it()
    {
        var (client, sms) = WithSms();
        sms.RejectWith = "Twilio 400 (63003): the number is not on WhatsApp";

        var response = await client.PostAsJsonAsync(Url, Request());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("sms.rejected", await Title(response));
    }

    [Fact]
    public async Task A_provider_down_is_503()
    {
        var (client, sms) = WithSms();
        sms.Down = true;

        var response = await client.PostAsJsonAsync(Url, Request());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("sms.unavailable", await Title(response));
    }

    [Fact]
    public async Task With_the_channel_off_it_is_503_sms_disabled()
    {
        var client = _factory.CreateClient();   // no Sms:Provider
        client.DefaultRequestHeaders.Add("X-Internal-Key", Key);

        var response = await client.PostAsJsonAsync(Url, Request());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("sms.disabled", await Title(response));
    }
}
