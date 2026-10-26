using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SyWater.Notifications.Api.Composition;
using SyWater.Notifications.Domain.Notifications;

namespace SyWater.Notifications.Api.Tests;

public sealed class ShortMessageChannelsApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public ShortMessageChannelsApiTests() => _factory.CreateSchema();

    public void Dispose() => _factory.Dispose();

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public async Task Any_user_learns_which_modes_can_arrive()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.TokenFor(Guid.NewGuid(), Roles.User));

        var view = await client.GetFromJsonAsync<JsonElement>("/api/short-messages/channels");

        Assert.Equal(JsonValueKind.Array, view.GetProperty("channels").ValueKind);
    }

    [Fact]
    public void Twilio_only_counts_for_the_modes_whose_sender_is_configured()
    {
        var whatsAppOnly = ServiceCollectionExtensions.ActiveProviders(Config(
            ("Sms:Provider", "twilio"), ("Twilio:WhatsAppFrom", "+15551234567"), ("Twilio:WhatsAppTemplateSid", "HXalert")));
        Assert.Equal("", whatsAppOnly.Sms);
        Assert.Equal("twilio", whatsAppOnly.WhatsApp);
        Assert.True(whatsAppOnly.WhatsAppTemplate);

        var both = ServiceCollectionExtensions.ActiveProviders(Config(
            ("Sms:Provider", "twilio"), ("Twilio:SmsFrom", "+15550000000"), ("Twilio:WhatsAppFrom", "+15551234567")));
        Assert.Equal("twilio", both.Sms);
        Assert.False(both.WhatsAppTemplate);

        var meta = ServiceCollectionExtensions.ActiveProviders(Config(("Sms:Provider", "mailpit"), ("WhatsApp:Provider", "meta")));
        Assert.Equal("mailpit", meta.Sms);
        Assert.Equal("meta", meta.WhatsApp);
    }

    [Fact]
    public void WhatsApp_by_Twilio_can_go_with_SMS_simulated_in_Mailpit()
    {
        var p = ServiceCollectionExtensions.ActiveProviders(Config(
            ("Sms:Provider", "mailpit"), ("WhatsApp:Provider", "twilio"), ("Twilio:WhatsAppFrom", "+16187025507"), ("Twilio:WhatsAppTemplateSid", "HXa")));
        Assert.Equal("mailpit", p.Sms);
        Assert.Equal("twilio", p.WhatsApp);
        Assert.True(p.WhatsAppTemplate);
    }

    [Fact]
    public async Task The_app_starts_with_WhatsApp_by_Twilio_and_says_so()
    {
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Sms:Provider", "mailpit");
            b.UseSetting("WhatsApp:Provider", "twilio");
            b.UseSetting("Twilio:AccountSid", "ACtest");
            b.UseSetting("Twilio:AuthToken", "secret");
            b.UseSetting("Twilio:WhatsAppFrom", "+16187025507");
        }).CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.TokenFor(Guid.NewGuid(), Roles.Admin));

        var providers = await client.GetFromJsonAsync<JsonElement>("/api/admin/message-templates/providers");

        Assert.Equal("twilio", providers.GetProperty("whatsApp").GetString());
        Assert.Equal("mailpit", providers.GetProperty("sms").GetString());
    }
}
