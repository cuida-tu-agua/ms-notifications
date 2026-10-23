namespace SyWater.Notifications.Application.UseCases;

/// <summary>
/// Dates shown to people (e-mail, alert text) in Colombia time: UTC-5 all year, also mainland Ecuador.
/// The database keeps storing UTC; only the text changes.
/// </summary>
public static class LocalTime
{
    private static readonly TimeZoneInfo Zone = Find();

    public static string Format(DateTime utc) =>
        $"{TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone):yyyy-MM-dd HH:mm} (hora Colombia)";

    private static TimeZoneInfo Find()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Without the time zone database Colombia is a fixed UTC-5 (no daylight saving time)
            return TimeZoneInfo.CreateCustomTimeZone("COT", TimeSpan.FromHours(-5), "Colombia", "Colombia");
        }
    }
}