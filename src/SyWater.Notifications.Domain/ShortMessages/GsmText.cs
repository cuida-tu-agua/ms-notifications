using System.Globalization;
using System.Text;

namespace SyWater.Notifications.Domain.ShortMessages;

/// <summary>
/// An SMS of 160 characters only exists in the GSM-7 alphabet. One accented letter outside it ("á", "í", "ó", "ú") turns
/// the whole message into UCS-2, where a segment holds only 70 characters: the same alert would cost 2 or 3 SMS. So the
/// SMS text drops those accents (keeping ñ / Ñ, which GSM-7 has). WhatsApp has no such limit and keeps the original text.
/// </summary>
public static class GsmText
{
    /// <summary>One SMS segment in GSM-7.</summary>
    public const int SegmentLength = 160;

    public static string ForSms(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is 'ñ' or 'Ñ' or 'é' or 'É') { builder.Append(c); continue; }   // both exist in GSM-7
            var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
            foreach (var part in decomposed)
                if (CharUnicodeInfo.GetUnicodeCategory(part) != UnicodeCategory.NonSpacingMark) builder.Append(part);
        }
        return builder.ToString()
            .Replace('¿', '?').Replace('¡', '!')   // not in GSM-7 as such: keep the sentence readable
            .Replace('—', '-').Replace('–', '-')
            .Normalize(NormalizationForm.FormC);
    }

    /// <summary>Cuts at a word boundary and adds "..." when the text does not fit.</summary>
    public static string Fit(string text, int max = SegmentLength)
    {
        if (text.Length <= max) return text;
        var cut = text[..(max - 3)];
        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut).TrimEnd(' ', '.', ',', ':') + "...";
    }
}
