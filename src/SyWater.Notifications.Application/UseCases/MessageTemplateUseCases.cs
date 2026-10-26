using SyWater.Notifications.Application.Ports.In;
using SyWater.Notifications.Application.Ports.Out;
using SyWater.Notifications.Application.Views;
using SyWater.Notifications.Domain.Messages;

namespace SyWater.Notifications.Application.UseCases;

/// <summary>Every message, in the order of the catalog, with the text it uses right now.</summary>
public sealed class ListMessageTemplatesUseCase(IMessageTemplateRepository templates) : IListMessageTemplatesUseCase
{
    public async Task<IReadOnlyList<MessageTemplateView>> ExecuteAsync(CancellationToken ct)
    {
        var custom = (await templates.ListCustomAsync(ct)).ToDictionary(t => t.Key);
        return MessageTemplateCatalog.All
            .Select(d => MessageTemplateView.From(custom.GetValueOrDefault(d.Key) ?? MessageTemplate.Default(d.Key)))
            .ToList();
    }
}

/// <summary>Saves the new text (checked by the domain). Saving exactly the default text goes back to the default one.</summary>
public sealed class UpdateMessageTemplateUseCase(IMessageTemplateRepository templates, TimeProvider clock) : IUpdateMessageTemplateUseCase
{
    public async Task<MessageTemplateView> ExecuteAsync(Guid adminId, string key, string? body, CancellationToken ct)
    {
        var parsed = MessageTemplateKeys.Parse(key) ?? throw new MessageTemplateNotFoundException(key);
        var template = MessageTemplate.Custom(parsed, body, adminId, clock.GetUtcNow().UtcDateTime);

        if (template.Body == template.Definition.DefaultBody)
        {
            await templates.DeleteAsync(parsed, ct);
            return MessageTemplateView.From(MessageTemplate.Default(parsed));
        }

        await templates.SaveAsync(template, ct);
        return MessageTemplateView.From(template);
    }
}

public sealed class ResetMessageTemplateUseCase(IMessageTemplateRepository templates) : IResetMessageTemplateUseCase
{
    public async Task<MessageTemplateView> ExecuteAsync(string key, CancellationToken ct)
    {
        var parsed = MessageTemplateKeys.Parse(key) ?? throw new MessageTemplateNotFoundException(key);
        await templates.DeleteAsync(parsed, ct);
        return MessageTemplateView.From(MessageTemplate.Default(parsed));
    }
}

/// <summary>Only the texts the app fills in itself are readable by everybody; the rest stay in the admin panel.</summary>
public sealed class GetSharedMessageTemplateUseCase(MessageTexts texts) : IGetSharedMessageTemplateUseCase
{
    private static readonly MessageTemplateKey[] Shared = [MessageTemplateKey.GuestLinkShare];

    public async Task<MessageTemplateView> ExecuteAsync(string key, CancellationToken ct)
    {
        var parsed = MessageTemplateKeys.Parse(key);
        if (parsed is not { } found || !Shared.Contains(found)) throw new MessageTemplateNotFoundException(key);
        return MessageTemplateView.From(await texts.GetAsync(found, ct));
    }
}
