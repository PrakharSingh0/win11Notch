namespace Win11Notch.Core.Settings;

public sealed record TimeGreetingInfo(
    string PeriodKey,
    string IconGlyph,
    string GreetingWord,
    string DisplayTitle,
    string Tagline,
    string AccentHex);

/// <summary>
/// Resolves expressive time-of-day greetings, Fluent glyphs, and accent colors cleanly across the app.
/// </summary>
public static class GreetingHelper
{
    public static TimeGreetingInfo GetGreeting(DateTime now)
    {
        var (key, glyph, word, tagline, hex) = now.Hour switch
        {
            >= 5 and < 9   => ("EarlyMorning", "\uE706", "Good Morning",   "Early start",       "#FBBF24"),
            >= 9 and < 12  => ("Morning",      "\uE706", "Good Morning",   "Focused hours",     "#FFD54F"),
            >= 12 and < 14 => ("Noon",         "\uE706", "Good Afternoon", "Midday check-in",   "#FFA726"),
            >= 14 and < 17 => ("Afternoon",    "\uE706", "Good Afternoon", "Afternoon flow",    "#FF8A50"),
            >= 17 and < 21 => ("Evening",      "\uE708", "Good Evening",   "Evening wind-down", "#FF7A59"),
            >= 21 and <= 23 => ("Night",       "\uE708", "Good Night",     "Take it easy",      "#A78BFA"),
            _              => ("LateNight",    "\uE708", "Late Night",     "Quiet hours",       "#60A5FA")
        };

        string name = ResolveUserName();
        string displayTitle = string.IsNullOrEmpty(name) ? word : $"{word}, {name}";
        return new TimeGreetingInfo(key, glyph, word, displayTitle, tagline, hex);
    }

    public static string ResolveUserName()
    {
        try
        {
            var raw = Environment.UserName?.Trim();
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var first = raw.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? raw;
            if (first.Length > 14) first = first[..14];
            return first.Length == 1
                ? char.ToUpperInvariant(first[0]).ToString()
                : char.ToUpperInvariant(first[0]) + first[1..];
        }
        catch
        {
            return string.Empty;
        }
    }
}
