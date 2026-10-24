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

/// <summary>The admin sends one message of a text to their own verified phone, with example data, to check the provider.</summary>
public interface ISendTestMessageUseCase
{
    Task<TestMessageView> ExecuteAsync(Guid adminId, string key, CancellationToken ct);
}

/// <summary>Which provider SMS and WhatsApp use in this environment.</summary>
public interface IGetShortMessageProvidersUseCase
{
    ShortMessageProvidersView Execute();
}

/// <summary>The modes (WHATSAPP / SMS) that can really reach a phone in this environment.</summary>
public interface IGetShortMessageChannelsUseCase
{
    ShortMessageChannelsView Execute();
}
