using System.Globalization;

namespace Beamcast;

/// <summary>Resolves the stored language choice against the OS and the two app locales.</summary>
public static class AppLanguage
{
    public const string System = "System";
    public const string Portuguese = "pt-BR";
    public const string English = "en";

    public static string Resolve(string? stored, string? osCulture = null)
    {
        if (Is(stored, Portuguese))
            return Portuguese;
        if (Is(stored, English))
            return English;
        return FromOs(osCulture);
    }

    public static string FromOs(string? osCulture = null)
    {
        var name = string.IsNullOrWhiteSpace(osCulture)
            ? CultureInfo.InstalledUICulture.Name
            : osCulture.Trim();
        if (name.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
            return Portuguese;
        return English;
    }

    private static bool Is(string? value, string expected) =>
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}
