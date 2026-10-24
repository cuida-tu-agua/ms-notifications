namespace SyWater.Notifications.Application.Ports.Out;

/// <summary>
/// HU-047 · Who besides the owner must hear about an alert of a place: the collaborators the owner gave the
/// VIEW_ALERTS permission. ms-places is the authority on sharing, so it is asked every time (a revoked collaborator
/// stops receiving alerts at once).
/// </summary>
public interface IPlaceAlertRecipients
{
    /// <summary>The collaborators with VIEW_ALERTS in the place (never the owner). Empty if the place is gone.</summary>
    /// <exception cref="ExternalServiceUnavailableException">ms-places did not answer: try again later.</exception>
    Task<IReadOnlyList<Guid>> CollaboratorsWithAlertsAsync(Guid placeId, CancellationToken ct);
}
