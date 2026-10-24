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
}
