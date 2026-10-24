using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.Notifications;

namespace SyWater.Notifications.Api.Tests;

public sealed class MessageTemplatesApiTests : IDisposable
{
    private const string Admin = "/api/admin/message-templates";
    private readonly ApiFactory _factory = new();

    public MessageTemplatesApiTests()
    {
        _factory.CreateSchema();
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient Client(Guid user, params string[] roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.TokenFor(user, roles));
        return client;
    }

    private static async Task<string?> Title(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

    [Fact]
    public async Task Only_an_admin_sees_and_edits_the_texts()
    {
        var user = Client(Guid.NewGuid(), Roles.User);

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync(Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsJsonAsync($"{Admin}/ALERT_CRITICAL", new { body = "x {title}" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.DeleteAsync($"{Admin}/ALERT_CRITICAL")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(Admin)).StatusCode);
    }

    [Fact]
    public async Task An_admin_edits_a_text_and_restores_the_default_one()
    {
        var admin = Client(Guid.NewGuid(), Roles.User, Roles.Admin);

        var list = await admin.GetFromJsonAsync<JsonElement>(Admin);
        Assert.Equal(4, list.GetArrayLength());
        Assert.False(list[0].GetProperty("isCustom").GetBoolean());

        var saved = await admin.PutAsJsonAsync($"{Admin}/ALERT_CRITICAL", new { body = "URGENTE en {place}: {title}" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var view = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(view.GetProperty("isCustom").GetBoolean());
        Assert.Equal("URGENTE en {place}: {title}", view.GetProperty("body").GetString());
        Assert.Equal(320, view.GetProperty("maxLength").GetInt32());

        var after = await admin.GetFromJsonAsync<JsonElement>(Admin);
        Assert.Equal("URGENTE en {place}: {title}", after[0].GetProperty("body").GetString());

        var reset = await admin.DeleteAsync($"{Admin}/ALERT_CRITICAL");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.False((await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isCustom").GetBoolean());
    }

    [Fact]
    public async Task A_text_that_cannot_be_used_is_400_and_an_unknown_message_404()
    {
        var admin = Client(Guid.NewGuid(), Roles.Admin);

        var invalid = await admin.PutAsJsonAsync($"{Admin}/PHONE_CODE", new { body = "Tu código llegó" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("message_template.invalid", await Title(invalid));

        var unknown = await admin.PutAsJsonAsync($"{Admin}/WELCOME", new { body = "Hola" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("message_template.not_found", await Title(unknown));
    }

    [Fact]
    public async Task Any_user_reads_the_guest_link_text_but_not_the_admin_ones()
    {
        var user = Client(Guid.NewGuid(), Roles.User);

        var guest = await user.GetFromJsonAsync<JsonElement>("/api/message-templates/GUEST_LINK_SHARE");
        Assert.Contains("{link}", guest.GetProperty("body").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync("/api/message-templates/PHONE_CODE")).StatusCode);
    }
}

public sealed class InternalInvitationsApiTests : IDisposable
{
    private const string Key = "test-internal-key-0123456789";
    private readonly ApiFactory _factory = new();

    public InternalInvitationsApiTests()
    {
        _factory.CreateSchema();
    }

    public void Dispose() => _factory.Dispose();

    private (HttpClient Client, FakeShortMessages Sms) Internal(string? key = Key)
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

    private HttpClient As(Guid user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.TokenFor(user, Roles.User));
        return client;
    }

    [Fact]
    public async Task Without_the_internal_key_it_is_403()
    {
        var (client, _) = Internal(key: null);

        var response = await client.PostAsJsonAsync("/internal/invitations", new
        {
            invitationId = Guid.NewGuid(), placeId = Guid.NewGuid(), placeName = "Casa", inviteeUserId = Guid.NewGuid(),
            invitedByName = "Ana", permissions = new[] { "VIEW_CONSUMPTION" }, expiresAt = DateTime.UtcNow.AddDays(7),
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_invited_person_finds_it_in_the_notification_center_and_the_owner_learns_the_answer()
    {
        var (client, _) = Internal();
        var invitee = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var invitation = Guid.NewGuid();
        var place = Guid.NewGuid();

        var sent = await client.PostAsJsonAsync("/internal/invitations", new
        {
            invitationId = invitation, placeId = place, placeName = "Casa centro", inviteeUserId = invitee,
            invitedByName = "Ana Gómez", permissions = new[] { "VIEW_CONSUMPTION", "VIEW_ALERTS" }, expiresAt = DateTime.UtcNow.AddDays(7),
        });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var result = await sent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("inApp").GetBoolean());
        Assert.True(result.GetProperty("shortMessageQueued").GetBoolean());

        var inbox = await As(invitee).GetFromJsonAsync<JsonElement>("/api/notifications");
        var n = Assert.Single(inbox.EnumerateArray());
        Assert.Equal("COLLABORATOR_INVITATION", n.GetProperty("type").GetString());
        Assert.Contains("Ana Gómez", n.GetProperty("body").GetString());

        var answered = await client.PostAsJsonAsync("/internal/invitations/answered", new
        {
            invitationId = invitation, placeId = place, placeName = "Casa centro", ownerId = owner, inviteeName = "Luis", accepted = false,
        });
        Assert.Equal(HttpStatusCode.Accepted, answered.StatusCode);
        var ownerInbox = await As(owner).GetFromJsonAsync<JsonElement>("/api/notifications");
        var answer = Assert.Single(ownerInbox.EnumerateArray());
        Assert.Equal("INVITATION_ANSWERED", answer.GetProperty("type").GetString());
        Assert.Equal("Invitación rechazada", answer.GetProperty("title").GetString());
    }

    [Fact]
    public async Task An_invitation_to_an_email_without_account_does_nothing_here()
    {
        var (client, _) = Internal();

        var sent = await client.PostAsJsonAsync("/internal/invitations", new
        {
            invitationId = Guid.NewGuid(), placeId = Guid.NewGuid(), placeName = "Casa", inviteeUserId = (Guid?)null,
            invitedByName = "Ana", permissions = new[] { "VIEW_CONSUMPTION" }, expiresAt = DateTime.UtcNow.AddDays(7),
        });

        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        Assert.False((await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("inApp").GetBoolean());
    }
}
