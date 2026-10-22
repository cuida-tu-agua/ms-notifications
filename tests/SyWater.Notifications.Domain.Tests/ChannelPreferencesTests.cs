using SyWater.Notifications.Domain.Notifications;
using SyWater.Notifications.Domain.Preferences;

namespace SyWater.Notifications.Domain.Tests;

public class ChannelPreferencesTests
{
    [Fact]
    public void Defaults_follow_the_channel_matrix()
    {
        var prefs = ChannelPreferences.Defaults();

        Assert.Equal(NotificationChannels.All, prefs.ChannelsFor(NotificationSeverity.Critical));
        Assert.Equal(NotificationChannels.InApp | NotificationChannels.Push, prefs.ChannelsFor(NotificationSeverity.Warning));
        Assert.Equal(NotificationChannels.InApp, prefs.ChannelsFor(NotificationSeverity.Info));
    }

    [Fact]
    public void Critical_always_keeps_in_app()
    {
        var prefs = ChannelPreferences.Defaults();

        Assert.Throws<CriticalChannelRequiredException>(() => prefs.Set(NotificationSeverity.Critical, NotificationChannels.Email));
        Assert.Throws<CriticalChannelRequiredException>(() => prefs.Set(NotificationSeverity.Critical, NotificationChannels.None));
        prefs.Set(NotificationSeverity.Critical, NotificationChannels.InApp);
        Assert.Equal(NotificationChannels.InApp, prefs.ChannelsFor(NotificationSeverity.Critical));
    }

    [Fact]
    public void A_level_can_be_changed_without_touching_the_others()
    {
        var prefs = ChannelPreferences.Defaults();
        prefs.Set(NotificationSeverity.Warning, NotificationChannels.None);

        Assert.Equal(NotificationChannels.None, prefs.ChannelsFor(NotificationSeverity.Warning));
        Assert.Equal(NotificationChannels.InApp, prefs.ChannelsFor(NotificationSeverity.Info));
    }

    [Theory]
    [InlineData(NotificationSeverity.Warning)]
    [InlineData(NotificationSeverity.Info)]
    public void Sms_can_only_be_turned_on_for_critical_alerts(NotificationSeverity severity)
    {
        var prefs = ChannelPreferences.Defaults();

        var error = Assert.Throws<SmsOnlyForCriticalException>(() => prefs.Set(severity, NotificationChannels.InApp | NotificationChannels.Sms));
        Assert.Equal("preferences.sms_only_critical", error.Code);
        Assert.False(prefs.ChannelsFor(severity).HasFlag(NotificationChannels.Sms));   // a rejected change changes nothing

        prefs.Set(NotificationSeverity.Critical, NotificationChannels.InApp | NotificationChannels.Sms);
        Assert.True(prefs.ChannelsFor(NotificationSeverity.Critical).HasFlag(NotificationChannels.Sms));
    }

    [Fact]
    public void An_old_row_with_sms_on_a_non_critical_level_never_shows_it()
    {
        var prefs = ChannelPreferences.Restore(new Dictionary<NotificationSeverity, NotificationChannels>
        {
            [NotificationSeverity.Warning] = NotificationChannels.InApp | NotificationChannels.Sms,
            [NotificationSeverity.Info] = NotificationChannels.All,
        });

        Assert.Equal(NotificationChannels.InApp, prefs.ChannelsFor(NotificationSeverity.Warning));
        Assert.False(prefs.ChannelsFor(NotificationSeverity.Info).HasFlag(NotificationChannels.Sms));
    }
}
