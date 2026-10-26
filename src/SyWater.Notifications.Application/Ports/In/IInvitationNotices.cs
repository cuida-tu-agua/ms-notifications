using SyWater.Notifications.Application.Views;

namespace SyWater.Notifications.Application.Ports.In;

/// <summary>
/// HU-042: ms-places tells that an owner invited a person to collaborate. A person WITH an account gets it in the
/// notification center (and push), and by WhatsApp / SMS if their phone is verified; the e-mail is sent by ms-iam.
/// </summary>
public interface INotifyCollaboratorInvitationUseCase
{
    Task<InvitationNoticeView> ExecuteAsync(CollaboratorInvitationNotice notice, CancellationToken ct);
}

/// <summary>HU-043: ms-places tells that the invited person accepted or rejected: the owner learns it in the app.</summary>
public interface INotifyInvitationAnsweredUseCase
{
    Task<InvitationNoticeView> ExecuteAsync(InvitationAnsweredNotice notice, CancellationToken ct);
}

/// <param name="InviteeUserId">The account of the invited e-mail, or null if that person has no account yet.</param>
/// <param name="Permissions">Permission keys of ms-places (VIEW_CONSUMPTION, CONTROL_VALVE...).</param>
public sealed record CollaboratorInvitationNotice(
    Guid InvitationId, Guid PlaceId, string? PlaceName, Guid? InviteeUserId, string? InviterName,
    IReadOnlyList<string> Permissions, DateTime ExpiresAt);

public sealed record InvitationAnsweredNotice(
    Guid InvitationId, Guid PlaceId, string? PlaceName, Guid OwnerId, string? InviteeName, bool Accepted);
