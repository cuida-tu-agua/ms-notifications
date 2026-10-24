using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SyWater.Notifications.Api.Security;
using SyWater.Notifications.Application.Ports.In;
using SyWater.Notifications.Application.Views;

namespace SyWater.Notifications.Api.Controllers;

/// <summary>
/// Admin panel · the texts of the WhatsApp / SMS messages (critical alert, phone code, collaborator invitation, guest link).
/// Only the ADMIN role of the token gets in. A change applies to the next message.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[Route("api/admin/message-templates")]
public sealed class AdminMessageTemplatesController : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MessageTemplateView>>> List(
        [FromServices] IListMessageTemplatesUseCase useCase, CancellationToken ct) =>
        Ok(await useCase.ExecuteAsync(ct));

    /// <summary>
    /// 400 message_template.invalid: empty, longer than 320, a placeholder this message does not have, or a required one
    /// removed ({code} in PHONE_CODE, {link} in GUEST_LINK_SHARE) · 404 message_template.not_found: unknown key.
    /// </summary>
    [HttpPut("{key}")]
    public async Task<ActionResult<MessageTemplateView>> Update(
        string key, SaveMessageTemplateRequest request, [FromServices] IUpdateMessageTemplateUseCase useCase, CancellationToken ct) =>
        Ok(await useCase.ExecuteAsync(User.GetRequester().UserId, key, request.Body, ct));

    /// <summary>Back to the default text; answers it.</summary>
    [HttpDelete("{key}")]
    public async Task<ActionResult<MessageTemplateView>> Reset(
        string key, [FromServices] IResetMessageTemplateUseCase useCase, CancellationToken ct) =>
        Ok(await useCase.ExecuteAsync(key, ct));

    public sealed record SaveMessageTemplateRequest(string? Body);
}

/// <summary>The texts the app fills in itself, for any signed-in user (today: the guest link shared by WhatsApp, HU-039).</summary>
[ApiController]
[Route("api/message-templates")]
public sealed class MessageTemplatesController : ControllerBase
{
    [HttpGet("{key}")]
    public async Task<ActionResult<MessageTemplateView>> Get(
        string key, [FromServices] IGetSharedMessageTemplateUseCase useCase, CancellationToken ct) =>
        Ok(await useCase.ExecuteAsync(key, ct));
}

/// <summary>Service-to-service only (X-Internal-Key): ms-places tells about invitations to collaborate (HU-042, HU-043).</summary>
[ApiController]
[AllowAnonymous]
[InternalKey]
[Route("internal/invitations")]
public sealed class InternalInvitationsController : ControllerBase
{
    /// <summary>An owner invited a person. 202 with what was done (nothing if the e-mail has no account).</summary>
    [HttpPost]
    public async Task<ActionResult<InvitationNoticeView>> Sent(
        InvitationSentRequest body, [FromServices] INotifyCollaboratorInvitationUseCase useCase, CancellationToken ct) =>
        Accepted(await useCase.ExecuteAsync(new CollaboratorInvitationNotice(
            body.InvitationId, body.PlaceId, body.PlaceName, body.InviteeUserId, body.InvitedByName,
            body.Permissions ?? [], body.ExpiresAt), ct));

    /// <summary>The invited person accepted or rejected: the owner is told.</summary>
    [HttpPost("answered")]
    public async Task<ActionResult<InvitationNoticeView>> Answered(
        InvitationAnsweredRequest body, [FromServices] INotifyInvitationAnsweredUseCase useCase, CancellationToken ct) =>
        Accepted(await useCase.ExecuteAsync(new InvitationAnsweredNotice(
            body.InvitationId, body.PlaceId, body.PlaceName, body.OwnerId, body.InviteeName, body.Accepted), ct));

    public sealed record InvitationSentRequest(
        Guid InvitationId, Guid PlaceId, string? PlaceName, Guid? InviteeUserId, string? InvitedByName,
        IReadOnlyList<string>? Permissions, DateTime ExpiresAt);

    public sealed record InvitationAnsweredRequest(
        Guid InvitationId, Guid PlaceId, string? PlaceName, Guid OwnerId, string? InviteeName, bool Accepted);
}
