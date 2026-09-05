namespace Beamcast.Audio;

/// <summary>What the broadcaster shares, sound-wise.</summary>
public static class AudioMode
{
    /// <summary>Window share → that app; screen share → everything except voice apps.</summary>
    public const string Auto = "Auto";
    public const string System = "System";
    public const string App = "App";
    public const string Off = "Off";

    public static readonly string[] All = [Auto, System, App, Off];

    public static string Normalize(string? value) =>
        All.FirstOrDefault(v => string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Auto;
}

/// <summary>
/// Executables whose sound must never be re-broadcast: they are the voice call the viewers are
/// already in. Matching is by process name (lower-case, no extension) on the process and its
/// ancestors, so helper/child processes of these apps are covered too.
/// </summary>
public static class VoiceApps
{
    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord", "discordptb", "discordcanary",
        "teams", "ms-teams", "msteams",
        "zoom", "cpthost",
        "slack",
        "whatsapp",
        "skype",
        "telegram",
        "webex", "webexhost", "atmgr",
        "signal",
        "element",
        "guilded",
        "teamspeak", "teamspeak3", "ts3client_win64", "ts3client_win32",
        "mumble",
        "beamcast",
    };

    public static bool IsVoiceApp(string? processName) =>
        !string.IsNullOrEmpty(processName) && Names.Contains(processName);
}

/// <summary>
/// Decides which process trees to capture for "system audio minus voice apps". Pure logic so it
/// can be unit tested: takes the audio sessions Windows reports and the process table.
/// </summary>
public static class AudioSourceSelector
{
    private const int MaxDepth = 32;

    /// <summary>
    /// Returns the root process ids whose trees should be captured with "include tree" mode.
    /// A session's process is dropped when it, or any ancestor, is a voice app or this process;
    /// a process is folded into an ancestor that is itself selected (its tree already covers it).
    /// </summary>
    public static IReadOnlyList<int> SelectSystemSources(
        IEnumerable<int> sessionPids,
        IReadOnlyDictionary<int, ProcessRow> processes,
        int selfPid
    )
    {
        var candidates = new List<int>();
        foreach (var pid in sessionPids.Distinct())
        {
            if (pid <= 4 || !processes.ContainsKey(pid))
                continue;
            if (IsExcluded(pid, processes, selfPid))
                continue;
            candidates.Add(pid);
        }

        var selected = new HashSet<int>(candidates);
        foreach (var pid in candidates)
        {
            foreach (var ancestor in Ancestors(pid, processes))
            {
                if (selected.Contains(ancestor))
                {
                    selected.Remove(pid);
                    break;
                }
            }
        }

        return selected.OrderBy(p => p).ToList();
    }

    /// <summary>True when the process or one of its ancestors is a voice app or ourselves.</summary>
    public static bool IsExcluded(int pid, IReadOnlyDictionary<int, ProcessRow> processes, int selfPid)
    {
        if (pid == selfPid)
            return true;
        if (processes.TryGetValue(pid, out var row) && VoiceApps.IsVoiceApp(row.Name))
            return true;
        foreach (var ancestor in Ancestors(pid, processes))
        {
            if (ancestor == selfPid)
                return true;
            if (processes.TryGetValue(ancestor, out var parent) && VoiceApps.IsVoiceApp(parent.Name))
                return true;
        }
        return false;
    }

    /// <summary>Human-readable list of the voice apps currently running, for the UI.</summary>
    public static IReadOnlyList<string> RunningVoiceApps(IReadOnlyDictionary<int, ProcessRow> processes) =>
        processes.Values
            .Where(r => VoiceApps.IsVoiceApp(r.Name) && r.Name != "beamcast")
            .Select(r => r.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

    private static IEnumerable<int> Ancestors(int pid, IReadOnlyDictionary<int, ProcessRow> processes)
    {
        var current = pid;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            if (!processes.TryGetValue(current, out var row))
                yield break;
            var parent = row.ParentPid;
            // Parent ids get recycled; a "parent" younger than the child is not really its parent,
            // but without start times we can only stop at obvious loops and the idle/system pids.
            if (parent <= 4 || parent == current || parent == pid)
                yield break;
            yield return parent;
            current = parent;
        }
    }
}
