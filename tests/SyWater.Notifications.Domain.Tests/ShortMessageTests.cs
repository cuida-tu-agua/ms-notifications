using SyWater.Notifications.Domain.ShortMessages;

namespace SyWater.Notifications.Domain.Tests;

public class ShortMessageTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    private readonly Guid _user = Guid.NewGuid();

    [Theory]
    [InlineData("SMS", ShortMessageChannel.Sms)]
    [InlineData("whatsapp", ShortMessageChannel.WhatsApp)]
    [InlineData(" WhatsApp ", ShortMessageChannel.WhatsApp)]
    public void The_mode_is_read_in_any_case(string text, ShortMessageChannel expected)
    {
        Assert.Equal(expected, ShortMessageChannels.Parse(text));
        Assert.Equal(text.Trim().ToUpperInvariant(), expected.ToCode());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TELEGRAM")]
    public void Any_other_mode_is_unknown(string? text) => Assert.Null(ShortMessageChannels.Parse(text));

    [Theory]
    [InlineData("+573001112233", true)]    // Colombia
    [InlineData("+593991112233", true)]    // Ecuador
    [InlineData("3001112233", false)]      // no country code
    [InlineData("+57 300 111 2233", false)]
    [InlineData("+0573001112233", false)]
    [InlineData(null, false)]
    public void Phones_must_be_E164(string? phone, bool valid) => Assert.Equal(valid, PhoneNumbers.IsE164(phone));

    [Fact]
    public void A_phone_is_masked_in_the_logs() => Assert.Equal("+57300***233", PhoneNumbers.Mask("+573001112233"));

    [Fact]
    public void Sms_text_drops_the_accents_that_would_cost_more_messages_but_keeps_the_enye()
    {
        Assert.Equal("Valvula cerrada en Piña: ?revisa! - año", GsmText.ForSms("Válvula cerrada en Piña: ¿revisa¡ — año"));
        Assert.Equal("Café", GsmText.ForSms("Café"));   // é exists in GSM-7
    }

    [Fact]
    public void A_long_text_is_cut_at_a_word_within_one_sms()
    {
        var text = string.Join(' ', Enumerable.Repeat("palabra", 40));
        var fit = GsmText.Fit(text);

        Assert.True(fit.Length <= GsmText.SegmentLength);
        Assert.EndsWith("...", fit);
        Assert.DoesNotContain("palabr...", fit);
        Assert.Equal("corto", GsmText.Fit("corto"));
    }

    [Fact]
    public void A_queued_message_is_pending_and_due_now()
    {
        var item = SmsOutboxItem.Queue(_user, " evt-1 ", "Hola", Now);

        Assert.Equal(SmsStatus.Pending, item.Status);
        Assert.Equal("evt-1", item.SourceEventId);
        Assert.True(item.IsDue(Now));
        Assert.Null(item.Channel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_message_is_rejected(string body) =>
        Assert.Throws<InvalidShortMessageException>(() => SmsOutboxItem.Queue(_user, null, body, Now));

    [Fact]
    public void A_message_longer_than_two_sms_is_rejected() =>
        Assert.Throws<InvalidShortMessageException>(() => SmsOutboxItem.Queue(_user, null, new string('a', 321), Now));

    [Fact]
    public void Sent_records_the_mode_and_the_provider_id_once()
    {
        var item = SmsOutboxItem.Queue(_user, "evt-1", "Hola", Now);
        item.MarkSent(ShortMessageChannel.WhatsApp, "SM123", Now.AddSeconds(5));

        Assert.Equal(SmsStatus.Sent, item.Status);
        Assert.Equal(ShortMessageChannel.WhatsApp, item.Channel);
        Assert.Equal("SM123", item.ProviderMessageId);
        Assert.Equal(1, item.Attempts);
        Assert.False(item.IsDue(Now.AddDays(1)));

        item.Skip("late");   // a finished message never changes again
        Assert.Equal(SmsStatus.Sent, item.Status);
    }

    [Fact]
    public void Failures_retry_after_1_5_15_60_minutes_and_the_fifth_is_final()
    {
        var item = SmsOutboxItem.Queue(_user, "evt-1", "Hola", Now);
        var policy = SmsRetryPolicy.Default;
        var expected = new[] { 1, 5, 15, 60 };

        for (var i = 0; i < 4; i++)
        {
            item.RegisterFailure("Twilio 503", Now, policy);
            Assert.Equal(SmsStatus.Pending, item.Status);
            Assert.Equal(Now.AddMinutes(expected[i]), item.NextAttemptAt);
        }

        item.RegisterFailure("Twilio 503", Now, policy);
        Assert.Equal(SmsStatus.Failed, item.Status);
        Assert.Equal(5, item.Attempts);
    }

    [Fact]
    public void Without_a_verified_phone_it_is_skipped_and_a_refused_number_fails_at_once()
    {
        var skipped = SmsOutboxItem.Queue(_user, "evt-1", "Hola", Now);
        skipped.Skip("The user has no verified phone.");
        Assert.Equal(SmsStatus.Skipped, skipped.Status);
        Assert.Equal(0, skipped.Attempts);

        var refused = SmsOutboxItem.Queue(_user, "evt-2", "Hola", Now);
        refused.MarkRejected(ShortMessageChannel.WhatsApp, "Twilio 400 (63003): not on WhatsApp");
        Assert.Equal(SmsStatus.Failed, refused.Status);
        Assert.Equal(ShortMessageChannel.WhatsApp, refused.Channel);
        Assert.Equal(1, refused.Attempts);
    }
}
