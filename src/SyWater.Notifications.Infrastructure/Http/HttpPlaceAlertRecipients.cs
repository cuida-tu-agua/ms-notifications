using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SyWater.Notifications.Application.Ports.Out;

namespace SyWater.Notifications.Infrastructure.Http;

/// <summary>
/// ms-places "GET /internal/places/{id}/recipients?permission=VIEW_ALERTS" with X-Internal-Key. Answer:
/// [{"userId","role"}] with the owner (OWNER) and the collaborators that hold the permission (COLLABORATOR).
/// 404 = the place does not exist any more. Only the collaborators are returned: the owner is notified by their own path.
/// </summary>
public sealed class HttpPlaceAlertRecipients(HttpClient http, string internalKey) : IPlaceAlertRecipients
{
    public const string ServiceName = "ms-places";
    public const string InternalKeyHeader = "X-Internal-Key";

    public async Task<IReadOnlyList<Guid>> CollaboratorsWithAlertsAsync(Guid placeId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"internal/places/{placeId}/recipients?permission=VIEW_ALERTS");
        request.Headers.Add(InternalKeyHeader, internalKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalServiceUnavailableException(ServiceName, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ExternalServiceUnavailableException(ServiceName, ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return [];
            if (!response.IsSuccessStatusCode) throw new ExternalServiceUnavailableException(ServiceName);

            try
            {
                var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (json.ValueKind != JsonValueKind.Array) throw new ExternalServiceUnavailableException(ServiceName);

                var collaborators = new List<Guid>();
                foreach (var item in json.EnumerateArray())
                {
                    var role = item.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                    var id = item.TryGetProperty("userId", out var u) && u.ValueKind == JsonValueKind.String && Guid.TryParse(u.GetString(), out var parsed)
                        ? parsed
                        : Guid.Empty;
                    if (role == "COLLABORATOR" && id != Guid.Empty) collaborators.Add(id);
                }
                return collaborators;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new ExternalServiceUnavailableException(ServiceName, ex);
            }
        }
    }
}
