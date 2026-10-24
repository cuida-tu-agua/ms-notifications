using System.Net;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Infrastructure.Http;

namespace SyWater.Notifications.Infrastructure.Tests;

public class HttpPlaceAlertRecipientsTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(answer(request));
        }
    }

    private static HttpPlaceAlertRecipients Recipients(Stub stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("http://places/") }, "internal-key-0123456789abcd");

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task It_asks_ms_places_for_the_alert_permission_with_the_internal_key_and_keeps_only_the_collaborators()
    {
        var place = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var ana = Guid.NewGuid();
        var stub = new Stub(_ => Json(HttpStatusCode.OK,
            $$"""[{"userId":"{{owner}}","role":"OWNER"},{"userId":"{{ana}}","role":"COLLABORATOR"}]"""));

        var result = await Recipients(stub).CollaboratorsWithAlertsAsync(place, default);

        Assert.Equal([ana], result);
        Assert.Equal($"/internal/places/{place}/recipients", stub.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("?permission=VIEW_ALERTS", stub.Last.RequestUri.Query);
        Assert.Equal("internal-key-0123456789abcd", stub.Last.Headers.GetValues("X-Internal-Key").Single());
    }

    [Fact]
    public async Task A_place_that_is_gone_has_no_recipients_and_ignores_malformed_entries()
    {
        Assert.Empty(await Recipients(new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound))).CollaboratorsWithAlertsAsync(Guid.NewGuid(), default));
        Assert.Empty(await Recipients(new Stub(_ => Json(HttpStatusCode.OK,
            """[{"role":"COLLABORATOR"},{"userId":"nope","role":"COLLABORATOR"},{"userId":"00000000-0000-0000-0000-000000000000","role":"COLLABORATOR"}]""")))
            .CollaboratorsWithAlertsAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Any_other_answer_means_ms_places_is_unavailable()
    {
        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            Recipients(new Stub(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))).CollaboratorsWithAlertsAsync(Guid.NewGuid(), default));
        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            Recipients(new Stub(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))).CollaboratorsWithAlertsAsync(Guid.NewGuid(), default));
        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            Recipients(new Stub(_ => Json(HttpStatusCode.OK, "not json"))).CollaboratorsWithAlertsAsync(Guid.NewGuid(), default));
        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            Recipients(new Stub(_ => Json(HttpStatusCode.OK, """{"unexpected":true}"""))).CollaboratorsWithAlertsAsync(Guid.NewGuid(), default));
    }
}
