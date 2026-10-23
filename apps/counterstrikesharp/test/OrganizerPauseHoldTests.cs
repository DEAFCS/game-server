using System.Runtime.CompilerServices;
using FiveStack;
using Xunit;

// DEAFCS: an organizer's pause (console/RCON, the website's Pause around a
// Restore Round) must not be lifted by the automatic resumes that run when
// players reconnect (match c23bfe4c, 2026-10-23: a reconnect resumed the map
// twice before the restore, and the round played meanwhile was recorded), and
// a backup load's player resets must not be recorded as kills and damage.
// Kept identical in both plugins' test projects.
public class OrganizerPauseHoldTests
{
    [Fact]
    public void AnAutomaticResumeLeavesAHeldOrganizerPause()
    {
        Assert.False(MatchManager.ResumeAllowed(organizerPauseHeld: true, automatic: true));
    }

    [Fact]
    public void AnExplicitResumeLiftsAHeldOrganizerPause()
    {
        Assert.True(MatchManager.ResumeAllowed(organizerPauseHeld: true, automatic: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithoutAnOrganizerPauseEveryResumeWorksAsBefore(bool automatic)
    {
        Assert.True(MatchManager.ResumeAllowed(organizerPauseHeld: false, automatic));
    }

    // The three reconnect/empty-team resumes are the automatic ones; an upstream
    // merge that drops the flag would silently bring the override back.
    [Theory]
    [InlineData("FiveStack.Events/PlayerConnected.cs")]
    [InlineData("FiveStack.Services/DisconnectBudgetSystem.cs")]
    [InlineData("FiveStack.Services/TeamEmptyForfeitSystem.cs")]
    public void ReconnectResumesAreAutomatic(string file)
    {
        string source = PluginSource(file);

        Assert.Contains("ResumeMatch(automatic: true)", source);
        Assert.DoesNotContain(".ResumeMatch();", source);
    }

    [Fact]
    public void AConsolePauseIsHeldEvenWhenAnEmptyTeamAlreadyPausedTheMatch()
    {
        string source = PluginSource("FiveStack.Services/TimeoutSystem.cs");
        int emptyTeamBranch = source.IndexOf("if (match.teamEmptyForfeitSystem.IsActivelyPausing)");
        int techPause = source.IndexOf("PauseTechMatch(pauseMessage);");

        Assert.True(emptyTeamBranch >= 0 && techPause > emptyTeamBranch);
        Assert.Equal(2, Count(source[emptyTeamBranch..techPause], "match.HoldOrganizerPause();"));
    }

    // The engine reports each player reset of mp_backup_restore_load_file as a
    // death and as damage; the guard has to come before the event is published.
    [Theory]
    [InlineData("FiveStack.Events/PlayerKills.cs", "\"kill\"")]
    [InlineData("FiveStack.Events/PlayerDamage.cs", "\"damage\"")]
    public void BackupLoadResetsAreNotRecorded(string file, string gameEvent)
    {
        string source = PluginSource(file);
        int guard = source.IndexOf("_gameBackupRounds.IsResettingRound()");
        int publish = source.IndexOf(gameEvent);

        Assert.True(guard >= 0, $"{file} has no IsResettingRound guard");
        Assert.True(publish > guard, $"{file} publishes {gameEvent} before the guard");
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value); at >= 0; at = text.IndexOf(value, at + value.Length))
        {
            count++;
        }
        return count;
    }

    private static string PluginSource(string relative, [CallerFilePath] string testFile = "")
    {
        string src = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "src"));
        return File.ReadAllText(Path.Combine(src, relative));
    }
}
