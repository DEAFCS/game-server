using FiveStack.Entities;
using FiveStack.Enums;
using FiveStack.Utilities;
using Xunit;

// DEAFCS regression cases for the Restore Round gates (5stackgg/game-server
// #253, #254, #255) as whole sequences: what the api's Restore Round
// (api-deafcs MatchRecoveryService) sends and reads back, refused restores,
// rounds a restore undid, restoring again after a replay, and an in-game
// .reset in warmup or knife. Kept identical in both plugins' test projects.
public class RestoreRoundRegressionTests
{
    // GameBackUpRounds.RestoreRefusal as DEAFCS calls it: no Rush mode, so
    // restores are never disabled by match type.
    private static string? Refusal(
        eMapStatus status,
        int round,
        IEnumerable<BackupRound> rounds,
        bool alreadyRestoring = false,
        int connected = 10,
        string type = "Competitive"
    ) =>
        RoundRestoreGateUtility.Refusal(
            false,
            alreadyRestoring,
            status,
            round,
            RoundRestoreGateUtility.LatestBackup(rounds, round) != null,
            connected,
            10,
            type
        );

    private static BackupRound Backup(int round, string file, string deletedAt = "") =>
        new BackupRound { round = round, backup_file = file, deleted_at = deletedAt };

    // Rounds 1-8 played, then the api undid 6-8 for a restore to round 5.
    private static List<BackupRound> RestoredToFive() =>
        Enumerable
            .Range(1, 8)
            .Select(round =>
                Backup(round, $"round {round}", round > 5 ? "2026-10-20T18:00:00Z" : "")
            )
            .ToList();

    // GameBackUpRounds.CheckForBackupRestore after a server restart: the
    // newest backup on disk that a restore didn't undo.
    private static int HighestKeptOnDisk(IEnumerable<int> onDisk, IEnumerable<BackupRound> rounds) =>
        onDisk
            .Where(round => !RoundRestoreGateUtility.IsUndone(rounds, round))
            .DefaultIfEmpty(0)
            .Max();

    // How the api reads a refusal off the RCON reply
    // (MatchRecoveryService.serverRefusal: after the prefix, trimmed, first line).
    private static string? ApiReads(string reply, string prefix)
    {
        int at = reply.IndexOf(prefix, StringComparison.Ordinal);
        return at == -1 ? null : reply[(at + prefix.Length)..].Trim().Split('\n')[0];
    }

    [Fact]
    public void RepliesWithThePrefixesTheRestoreRoundApiReads()
    {
        Assert.Equal("restore_round refused:", RoundRestoreGateUtility.RefusedPrefix);
        Assert.Equal("match_state refused:", RoundRestoreGateUtility.MatchStateRefusedPrefix);

        string reason = Refusal(eMapStatus.Live, 9, RestoredToFive())!;
        string reply = $"{RoundRestoreGateUtility.RefusedPrefix} {reason}\n";

        Assert.Equal("the server has no backup of round 9", ApiReads(reply, "restore_round refused:"));
    }

    [Fact]
    public void AnAcceptedRestoreRepliesNothingTheApiTakesForARefusal()
    {
        Assert.Null(Refusal(eMapStatus.Live, 5, RestoredToFive()));
        Assert.Null(ApiReads("", "restore_round refused:"));
    }

    [Theory]
    [InlineData("Competitive")]
    [InlineData("Wingman")]
    [InlineData("Duel")]
    public void NeverRefusesADeafcsMatchTypeForItsType(string type)
    {
        Assert.Null(Refusal(eMapStatus.Live, 5, RestoredToFive(), type: type));
    }

    [Fact]
    public void RefusesARestoreBeforeTheApiResetsAnything()
    {
        var rounds = RestoredToFive();

        Assert.Equal(
            "a round restore is already in progress",
            Refusal(eMapStatus.Live, 5, rounds, alreadyRestoring: true)
        );
        Assert.Equal(
            "waiting for all players to reconnect (9/10)",
            Refusal(eMapStatus.Live, 5, rounds, connected: 9)
        );
        Assert.Equal("the server has no backup of round 12", Refusal(eMapStatus.Live, 12, rounds));
        Assert.Equal("the map is already over", Refusal(eMapStatus.Finished, 5, rounds));
    }

    [Fact]
    public void ARoundARestoreUndidCanBeBroughtBackUntilTheNextRoundIsPlayed()
    {
        var rounds = RestoredToFive();

        Assert.True(RoundRestoreGateUtility.IsUndone(rounds, 7));
        Assert.Equal("round 7", RoundRestoreGateUtility.LatestBackup(rounds, 7)?.backup_file);
        Assert.Null(Refusal(eMapStatus.Paused, 7, rounds));
    }

    [Fact]
    public void ARestartedServerResumesOnTheRoundThatWasKept()
    {
        var rounds = RestoredToFive();

        Assert.Equal(5, HighestKeptOnDisk(Enumerable.Range(1, 8), rounds));
    }

    [Fact]
    public void RestoringAgainAfterAReplayLoadsTheReplayedRound()
    {
        var rounds = RestoredToFive();
        rounds.Add(Backup(6, "round 6 replayed"));

        Assert.Equal("round 6 replayed", RoundRestoreGateUtility.LatestBackup(rounds, 6)?.backup_file);
        // DEAFCS used FirstOrDefault before this port and loaded the stale backup.
        Assert.Equal("round 6", rounds.First(backup => backup.round == 6).backup_file);
        Assert.False(RoundRestoreGateUtility.IsUndone(rounds, 6));
        Assert.True(RoundRestoreGateUtility.IsUndone(rounds, 7));
        Assert.Equal(6, HighestKeptOnDisk(Enumerable.Range(1, 8), rounds));
        Assert.Null(Refusal(eMapStatus.Live, 6, rounds));
    }

    [Fact]
    public void RestoringTheSameRoundTwiceKeepsLoadingItsNewestBackup()
    {
        var rounds = RestoredToFive();
        rounds.Add(Backup(6, "round 6 second play"));
        rounds.Add(Backup(6, "round 6 third play"));

        Assert.Equal(
            "round 6 third play",
            RoundRestoreGateUtility.LatestBackup(rounds, 6)?.backup_file
        );
    }

    [Theory]
    [InlineData(eMapStatus.Warmup)]
    [InlineData(eMapStatus.Knife)]
    public void ADotResetInWarmupOrKnifeIsRefusedBeforeTheMatchPauses(eMapStatus status)
    {
        var rounds = RestoredToFive();

        Assert.Equal("the map isn't live yet", Refusal(status, 5, rounds));
        // The warmup reason wins over a missing player or a missing backup, so
        // the vote never starts and mp_pause_match is never sent.
        Assert.Equal("the map isn't live yet", Refusal(status, 12, rounds, connected: 3));
    }

    [Theory]
    [InlineData(eMapStatus.Warmup)]
    [InlineData(eMapStatus.Knife)]
    [InlineData(eMapStatus.Live)]
    [InlineData(eMapStatus.Overtime)]
    [InlineData(eMapStatus.Paused)]
    public void RestartMapStillSendsAMapBeingPlayedBackToWarmup(eMapStatus status)
    {
        Assert.Null(RoundRestoreGateUtility.WarmupRefusal(true, status));
    }
}
