namespace Beamcast;

/// <summary>Loads the bundled changelog for the active language.</summary>
public static class ChangelogStore
{
    public static string Read(string? language = null)
    {
        var lang = AppLanguage.Resolve(
            string.IsNullOrWhiteSpace(language) ? SettingsStore.Load().Language : language
        );
        var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "changelog");
        foreach (var name in new[] { $"{lang}.md", "en.md" })
        {
            var path = Path.Combine(folder, name);
            var text = SafeTry.Run(() => File.Exists(path) ? File.ReadAllText(path) : null);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return Loc.Get("Changelog_Missing");
    }
}
