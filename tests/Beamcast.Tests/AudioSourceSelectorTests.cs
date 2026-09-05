using Beamcast.Audio;
using Xunit;

namespace Beamcast.Tests;

public class AudioSourceSelectorTests
{
    private static Dictionary<int, ProcessRow> Table(params ProcessRow[] rows) => rows.ToDictionary(r => r.Pid);

    [Fact]
    public void ExcludesVoiceAppsAndTheirChildren()
    {
        var processes = Table(
            new ProcessRow(100, 4, "explorer"),
            new ProcessRow(200, 100, "discord"),
            new ProcessRow(201, 200, "discord"),
            new ProcessRow(300, 100, "game"),
            new ProcessRow(400, 100, "beamcast")
        );

        var selected = AudioSourceSelector.SelectSystemSources(new[] { 201, 300, 400 }, processes, selfPid: 400);
        Assert.Equal(new[] { 300 }, selected);
    }

    [Fact]
    public void FoldsChildrenIntoASelectedAncestor()
    {
        var processes = Table(
            new ProcessRow(100, 4, "explorer"),
            new ProcessRow(500, 100, "steam"),
            new ProcessRow(510, 500, "game"),
            new ProcessRow(520, 510, "gamechild")
        );

        var selected = AudioSourceSelector.SelectSystemSources(new[] { 500, 510, 520 }, processes, selfPid: 1);
        Assert.Equal(new[] { 500 }, selected);

        var withoutSteam = AudioSourceSelector.SelectSystemSources(new[] { 510, 520 }, processes, selfPid: 1);
        Assert.Equal(new[] { 510 }, withoutSteam);
    }

    [Fact]
    public void IgnoresUnknownAndSystemPids()
    {
        var processes = Table(new ProcessRow(100, 4, "explorer"));
        Assert.Empty(AudioSourceSelector.SelectSystemSources(new[] { 0, 4, 999 }, processes, selfPid: 1));
    }

    [Fact]
    public void SurvivesParentLoops()
    {
        var processes = Table(
            new ProcessRow(10, 20, "a"),
            new ProcessRow(20, 10, "b")
        );
        var selected = AudioSourceSelector.SelectSystemSources(new[] { 10, 20 }, processes, selfPid: 1);
        Assert.NotEmpty(selected);
        Assert.All(selected, pid => Assert.Contains(pid, new[] { 10, 20 }));
    }

    [Fact]
    public void ListsRunningVoiceApps()
    {
        var processes = Table(
            new ProcessRow(1, 0, "discord"),
            new ProcessRow(2, 0, "ms-teams"),
            new ProcessRow(3, 0, "beamcast"),
            new ProcessRow(4, 0, "notepad")
        );
        Assert.Equal(new[] { "discord", "ms-teams" }, AudioSourceSelector.RunningVoiceApps(processes));
    }
}
