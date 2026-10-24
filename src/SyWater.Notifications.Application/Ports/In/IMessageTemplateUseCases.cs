using SyWater.Notifications.Application.Views;

namespace SyWater.Notifications.Application.Ports.In;

/// <summary>Admin panel: every WhatsApp / SMS text with its default one and the placeholders it accepts.</summary>
public interface IListMessageTemplatesUseCase
{
    Task<IReadOnlyList<MessageTemplateView>> ExecuteAsync(CancellationToken ct);
}

/// <summary>An admin rewrites a text. It applies to the next message.</summary>
public interface IUpdateMessageTemplateUseCase
{
    Task<MessageTemplateView> ExecuteAsync(Guid adminId, string key, string? body, CancellationToken ct);
}

/// <summary>An admin goes back to the default text.</summary>
public interface IResetMessageTemplateUseCase
{
    Task<MessageTemplateView> ExecuteAsync(string key, CancellationToken ct);
}

/// <summary>
/// The texts the app itself fills in (any signed-in user): today only the guest link that the owner shares from their
/// own WhatsApp (HU-039).
/// </summary>
public interface IGetSharedMessageTemplateUseCase
{
    Task<MessageTemplateView> ExecuteAsync(string key, CancellationToken ct);
}
