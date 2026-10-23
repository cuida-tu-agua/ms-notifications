using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SyWater.Notifications.Application.Events;
using SyWater.Notifications.Application.Ports.In;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Domain.Notifications;

namespace SyWater.Notifications.Api.Tests;

public sealed class NotificationsApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();   // one app + database per test: broadcasts to a role must not leak between tests

    public NotificationsApiTests()
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
    public async Task Without_a_token_every_endpoint_answers_401_but_health_is_open()
    {
        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications/unread-count")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/notifications/read-all", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Each_person_sees_their_own_and_their_roles_notifications_only()
    {
        var juan = Guid.NewGuid();
        var ana = Guid.NewGuid();
        var admin = Guid.NewGuid();
        await _factory.SeedAsync(Audience.ForUser(juan), "for Juan");
        await _factory.SeedAsync(Audience.ForUser(ana), "for Ana");
        await _factory.SeedAsync(Audience.ForRole(Roles.Admin), "for admins");

        var juanInbox = await Client(juan, Roles.User).GetFromJsonAsync<JsonElement>("/api/notifications");
        Assert.Equal(["for Juan"], juanInbox.EnumerateArray().Select(n => n.GetProperty("title").GetString()));

        var adminTitles = (await Client(admin, Roles.User, Roles.Admin).GetFromJsonAsync<JsonElement>("/api/notifications"))
            .EnumerateArray().Select(n => n.GetProperty("title").GetString()).ToList();
        Assert.Contains("for admins", adminTitles);
        Assert.DoesNotContain("for Juan", adminTitles);
        Assert.DoesNotContain("for Ana", adminTitles);
    }

    [Fact]
    public async Task Read_flow_counter_goes_down_and_read_all_marks_the_rest()
    {
        var me = Guid.NewGuid();
        await _factory.SeedAsync(Audience.ForUser(me), "one", NotificationSeverity.Critical);
        await _factory.SeedAsync(Audience.ForUser(me), "two");
        var client = Client(me, Roles.User);

        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>("/api/notifications/unread-count")).GetProperty("count").GetInt32());

        var first = (await client.GetFromJsonAsync<JsonElement>("/api/notifications")).EnumerateArray()
            .Single(n => n.GetProperty("title").GetString() == "one");
        Assert.Equal("CRITICAL", first.GetProperty("severity").GetString());   // enums travel as strings
        var read = await client.PostAsync($"/api/notifications/{first.GetProperty("id").GetGuid()}/read", null);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.True((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isRead").GetBoolean());
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>("/api/notifications/unread-count")).GetProperty("count").GetInt32());
        Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/notifications?unreadOnly=true")).EnumerateArray());

        var all = await client.PostAsync("/api/notifications/read-all", null);
        Assert.Equal(1, (await all.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("marked").GetInt32());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/notifications/unread-count")).GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Marking_somebody_elses_notification_is_404_with_the_stable_code()
    {
        var owner = Guid.NewGuid();
        await _factory.SeedAsync(Audience.ForUser(owner), "private");
        var id = (await Client(owner, Roles.User).GetFromJsonAsync<JsonElement>("/api/notifications"))
            .EnumerateArray().Single().GetProperty("id").GetGuid();

        var stranger = await Client(Guid.NewGuid(), Roles.User, Roles.Admin).PostAsync($"/api/notifications/{id}/read", null);

        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        Assert.Equal("notification.not_found", await Title(stranger));
        Assert.Equal(1, (await Client(owner, Roles.User).GetFromJsonAsync<JsonElement>("/api/notifications/unread-count"))
            .GetProperty("count").GetInt32());   // the owner still has it unread
    }

    [Fact]
    public async Task A_token_without_roles_only_sees_personal_notifications()
    {
        var me = Guid.NewGuid();
        await _factory.SeedAsync(Audience.ForRole(Roles.User), "broadcast");
        await _factory.SeedAsync(Audience.ForUser(me), "mine");

        var titles = (await Client(me).GetFromJsonAsync<JsonElement>("/api/notifications"))
            .EnumerateArray().Select(n => n.GetProperty("title").GetString());

        Assert.Equal(["mine"], titles);
    }

    [Fact]
    public async Task A_valve_closed_event_reaches_the_owner_inbox_as_critical_and_nobody_elses()
    {
        var owner = Guid.NewGuid();
        var place = Guid.NewGuid();
        var device = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<IDeviceEventsHandler>();
            await handler.HandleAsync("evt-1", new DeviceLinkedEvent(device, "SW-ESP32-000001", place, owner, DateTime.UtcNow), default);
            await handler.HandleAsync("evt-2", new ValveReportedEvent(device, place, "CLOSED", null, DateTime.UtcNow.AddSeconds(1)), default);
        }

        var inbox = (await Client(owner, Roles.User).GetFromJsonAsync<JsonElement>("/api/notifications")).EnumerateArray().ToList();
        var closed = Assert.Single(inbox, n => n.GetProperty("severity").GetString() == "CRITICAL");
        Assert.Equal("VALVE_CHANGED", closed.GetProperty("type").GetString());
        Assert.Equal(place, closed.GetProperty("placeId").GetGuid());
        Assert.Empty((await Client(Guid.NewGuid(), Roles.User).GetFromJsonAsync<JsonElement>("/api/notifications")).EnumerateArray());
    }

    [Fact]
    public async Task Preferences_show_the_matrix_defaults_and_a_change_applies_to_the_next_notification()
    {
        var owner = Guid.NewGuid();
        var client = Client(owner, Roles.User);

        var defaults = (await client.GetFromJsonAsync<JsonElement>("/api/notification-preferences")).GetProperty("levels");
        var critical = defaults.EnumerateArray().Single(l => l.GetProperty("severity").GetString() == "CRITICAL");
        Assert.True(critical.GetProperty("email").GetBoolean());

        var off = await client.PutAsJsonAsync("/api/notification-preferences/info", new { inApp = false, push = false, email = false, sms = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);   // the route accepts the level in any case

        using (var scope = _factory.Services.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<IDeviceEventsHandler>();
            await handler.HandleAsync("evt-1", new DeviceLinkedEvent(Guid.NewGuid(), "SW-1", Guid.NewGuid(), owner, DateTime.UtcNow), default);
        }

        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/notifications")).EnumerateArray());   // the Info notice was switched off
    }

    [Fact]
    public async Task Turning_off_the_in_app_channel_of_critical_alerts_is_400_with_the_stable_code()
    {
        var response = await Client(Guid.NewGuid(), Roles.User)
            .PutAsJsonAsync("/api/notification-preferences/CRITICAL", new { inApp = false, push = true, email = true, sms = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("preferences.critical_requires_in_app", await Title(response));
    }

    [Theory]
    [InlineData("WARNING")]
    [InlineData("info")]
    public async Task Sms_on_a_non_critical_level_is_400_and_reads_back_off(string level)
    {
        var client = Client(Guid.NewGuid(), Roles.User);

        var response = await client.PutAsJsonAsync($"/api/notification-preferences/{level}", new { inApp = true, push = true, email = false, sms = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("preferences.sms_only_critical", await Title(response));
        var levels = (await client.GetFromJsonAsync<JsonElement>("/api/notification-preferences")).GetProperty("levels").EnumerateArray().ToList();
        Assert.All(levels.Where(l => l.GetProperty("severity").GetString() != "CRITICAL"), l => Assert.False(l.GetProperty("sms").GetBoolean()));
        Assert.True(levels.Single(l => l.GetProperty("severity").GetString() == "CRITICAL").GetProperty("sms").GetBoolean());
    }

    [Fact]
    public async Task A_phone_registers_and_unregisters_its_push_token()
    {
        var client = Client(Guid.NewGuid(), Roles.User);
        var body = new { token = "ExponentPushToken[api-phone]", platform = "android" };

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/push-tokens", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/push-tokens", body)).StatusCode);   // idempotent

        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/push-tokens") { Content = JsonContent.Create(new { token = body.token }) };
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);
    }

    [Fact]
    public async Task A_critical_alert_reaches_the_registered_phone_and_stops_after_it_is_unregistered()
    {
        var owner = Guid.NewGuid();
        var place = Guid.NewGuid();
        var client = Client(owner, Roles.User);
        await client.PostAsJsonAsync("/api/push-tokens", new { token = "ExponentPushToken[alert-phone]", platform = "ios" });

        using (var scope = _factory.Services.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<IDeviceEventsHandler>();
            var device = Guid.NewGuid();
            await handler.HandleAsync("evt-p1", new DeviceLinkedEvent(device, "SW-1", place, owner, DateTime.UtcNow), default);
            await handler.HandleAsync("evt-p2", new ValveReportedEvent(device, place, "CLOSED", null, DateTime.UtcNow.AddSeconds(1)), default);
        }

        var push = Assert.Single(_factory.Push.Sent);
        Assert.Equal("ExponentPushToken[alert-phone]", push.Token);
        Assert.Equal(place.ToString(), push.Data["placeId"]);

        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/push-tokens") { Content = JsonContent.Create(new { token = "ExponentPushToken[alert-phone]" }) };
        await client.SendAsync(delete);
        using (var scope = _factory.Services.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<IDeviceEventsHandler>();
            await handler.HandleAsync("evt-p3", new ValveReportedEvent(Guid.NewGuid(), place, "CLOSED", null, DateTime.UtcNow.AddSeconds(2)), default);
        }
        Assert.Single(_factory.Push.Sent);   // the logged-out phone gets nothing
    }

    [Fact]
    public async Task A_bad_push_token_or_platform_is_400_with_the_stable_code()
    {
        var client = Client(Guid.NewGuid(), Roles.User);

        var token = await client.PostAsJsonAsync("/api/push-tokens", new { token = "not-a-token", platform = "android" });
        Assert.Equal(HttpStatusCode.BadRequest, token.StatusCode);
        Assert.Equal("push.invalid_token", await Title(token));

        var platform = await client.PostAsJsonAsync("/api/push-tokens", new { token = "ExponentPushToken[x]", platform = "web" });
        Assert.Equal(HttpStatusCode.BadRequest, platform.StatusCode);
        Assert.Equal("push.invalid_platform", await Title(platform));
    }

    [Fact]
    public async Task Push_tokens_need_a_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().PostAsJsonAsync("/api/push-tokens", new { token = "ExponentPushToken[x]", platform = "ios" })).StatusCode);
    }

    [Fact]
    public async Task Preferences_need_a_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/notification-preferences")).StatusCode);
    }

    [Fact]
    public async Task A_valve_closed_event_queues_a_mail_that_the_worker_sends_to_the_address_ms_iam_gives()
    {
        var owner = Guid.NewGuid();
        var place = Guid.NewGuid();
        var device = Guid.NewGuid();
        _factory.Contacts.Known[owner] = new UserContact(owner, "juan@example.com", "Juan Ome");
        using var scope = _factory.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IDeviceEventsHandler>();
        await handler.HandleAsync("evt-1", new DeviceLinkedEvent(device, "SW-ESP32-000001", place, owner, DateTime.UtcNow), default);
        await handler.HandleAsync("evt-2", new ValveReportedEvent(device, place, "CLOSED", null, DateTime.UtcNow.AddSeconds(1)), default);
        await handler.HandleAsync("evt-2", new ValveReportedEvent(device, place, "CLOSED", null, DateTime.UtcNow.AddSeconds(1)), default);   // redelivered

        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ISendDueEmailsUseCase>().ExecuteAsync(default));

        var mail = Assert.Single(_factory.Sender.Sent);   // Info "linked" does not mail by default; critical does, once
        Assert.Equal("juan@example.com", mail.ToAddress);
        Assert.Contains("Válvula cerrada", mail.Subject);
        Assert.Contains($"/places/{place}", mail.TextBody);
    }

    [Fact]
    public async Task A_silent_device_puts_one_important_alert_in_the_owner_inbox()
    {
        var owner = Guid.NewGuid();
        var place = Guid.NewGuid();
        var device = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<IDeviceEventsHandler>();
            await handler.HandleAsync("evt-1", new DeviceLinkedEvent(device, "SW-ESP32-000001", place, owner, DateTime.UtcNow), default);
            // its last reading was 11 minutes ago (server time)
            await handler.HandleAsync("evt-2", new ReadingReceivedEvent(device, place, DateTime.UtcNow, 1m, 0.1m, 10m, DateTime.UtcNow.AddMinutes(-11)), default);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var detector = scope.ServiceProvider.GetRequiredService<IDetectOfflineDevicesUseCase>();
            Assert.Equal(1, await detector.ExecuteAsync(default));
            Assert.Equal(0, await detector.ExecuteAsync(default));
        }

        var inbox = (await Client(owner, Roles.User).GetFromJsonAsync<JsonElement>("/api/notifications")).EnumerateArray().ToList();
        var alert = Assert.Single(inbox, n => n.GetProperty("type").GetString() == "DEVICE_OFFLINE");
        Assert.Equal("WARNING", alert.GetProperty("severity").GetString());
        Assert.Equal(place, alert.GetProperty("placeId").GetGuid());
    }
}
