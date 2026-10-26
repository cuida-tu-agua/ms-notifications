using SyWater.Notifications.Domain.Messages;

namespace SyWater.Notifications.Domain.Tests;

public class MessageTemplateTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 3, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Admin = Guid.NewGuid();

    [Fact]
    public void Every_message_has_a_default_text_that_only_uses_its_own_placeholders()
    {
        foreach (var definition in MessageTemplateCatalog.All)
        {
            var custom = MessageTemplate.Custom(definition.Key, definition.DefaultBody, Admin, Now);   // would throw if not
            Assert.Equal(definition.DefaultBody, custom.Body);
            Assert.False(MessageTemplate.Default(definition.Key).IsCustom);
        }
        Assert.Equal(4, MessageTemplateCatalog.All.Count);
    }

    [Fact]
    public void Rendering_fills_the_values_and_the_brand()
    {
        var text = MessageTemplate.Default(MessageTemplateKey.AlertCritical)
            .Render(new Dictionary<string, string?> { ["title"] = "Válvula cerrada", ["place"] = "Casa centro" });

        Assert.Equal("Cuida Tu Agua - ALERTA: Válvula cerrada en Casa centro. Abre la app para revisarla.", text);
    }

    [Fact]
    public void A_value_from_a_user_cannot_break_the_message_into_lines_and_a_missing_one_is_left_empty()
    {
        var template = MessageTemplate.Custom(MessageTemplateKey.AlertCritical, "Alerta en {place}: {title}", Admin, Now);

        var text = template.Render(new Dictionary<string, string?> { ["place"] = "Casa\n\rcentro" });

        Assert.Equal("Alerta en Casa centro:", text);
    }

    [Fact]
    public void A_long_result_is_cut_at_a_word_boundary()
    {
        var template = MessageTemplate.Custom(MessageTemplateKey.AlertCritical, "{title}", Admin, Now);

        var text = template.Render(new Dictionary<string, string?> { ["title"] = string.Join(' ', Enumerable.Repeat("palabra", 40)) }, 50);

        Assert.True(text.Length <= 50);
        Assert.EndsWith("...", text);
    }

    [Fact]
    public void A_custom_text_is_trimmed_and_keeps_who_saved_it()
    {
        var template = MessageTemplate.Custom(MessageTemplateKey.PhoneCode, "  Tu código es {code}  ", Admin, Now);

        Assert.True(template.IsCustom);
        Assert.Equal("Tu código es {code}", template.Body);
        Assert.Equal(Admin, template.UpdatedBy);
        Assert.Equal(Now, template.UpdatedAt);
    }

    [Theory]
    [InlineData(MessageTemplateKey.PhoneCode, "")]                                  // empty
    [InlineData(MessageTemplateKey.PhoneCode, "Tu código ya llegó")]                // {code} removed
    [InlineData(MessageTemplateKey.GuestLinkShare, "Mira mi consumo en {place}")]   // {link} removed
    [InlineData(MessageTemplateKey.AlertCritical, "Alerta en {lugar}")]             // not a placeholder of this message
    [InlineData(MessageTemplateKey.AlertCritical, "Alerta {code}")]                 // a placeholder of another message
    [InlineData(MessageTemplateKey.AlertCritical, "Alerta {title")]                 // not closed
    public void A_text_that_cannot_be_used_is_refused(MessageTemplateKey key, string body)
    {
        Assert.Throws<InvalidMessageTemplateException>(() => MessageTemplate.Custom(key, body, Admin, Now));
    }

    [Fact]
    public void A_text_longer_than_two_sms_is_refused()
    {
        Assert.Throws<InvalidMessageTemplateException>(() =>
            MessageTemplate.Custom(MessageTemplateKey.AlertCritical, new string('a', MessageTemplate.MaxBodyLength + 1), Admin, Now));
    }

    [Theory]
    [InlineData("ALERT_CRITICAL", MessageTemplateKey.AlertCritical)]
    [InlineData("phone-code", MessageTemplateKey.PhoneCode)]
    [InlineData("collaborator_invitation", MessageTemplateKey.CollaboratorInvitation)]
    [InlineData("GUEST_LINK_SHARE", MessageTemplateKey.GuestLinkShare)]
    public void Keys_are_read_in_any_case_and_written_back_upper_case(string text, MessageTemplateKey key)
    {
        Assert.Equal(key, MessageTemplateKeys.Parse(text));
        Assert.Equal(text.Replace('-', '_').ToUpperInvariant(), key.ToCode());
    }

    [Fact]
    public void An_unknown_key_is_null()
    {
        Assert.Null(MessageTemplateKeys.Parse("WELCOME"));
        Assert.Null(MessageTemplateKeys.Parse(null));
    }
}
