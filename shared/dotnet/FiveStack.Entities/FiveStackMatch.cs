namespace FiveStack.Entities;

public class MatchData
{
    public Guid id { get; set; } = Guid.Empty;
    public bool is_lan { get; set; } = false;
    public bool is_tournament_match { get; set; } = false;

    // Draft/pickup lobby (Open Match / AUTO-SPLIT) match -- see
    // is_draft_match.sql on the API side. Used to extend tournament-only
    // features (.tech technical pause) to draft matches too, while MM
    // stays excluded.
    public bool is_draft_match { get; set; } = false;
    public string password { get; set; } = "connectme";

    public Guid? current_match_map_id { get; set; } = Guid.Empty;

    // When the match will auto-cancel if players don't connect/ready up in
    // time. Drives the in-game milestone chat countdown; null means no
    // auto-cancel is pending.
    public DateTime? cancels_at { get; set; } = null;

    public MatchMap[] match_maps { get; set; } = new MatchMap[0];

    public MatchOptions options { get; set; } = new MatchOptions();

    public Guid lineup_1_id { get; set; } = Guid.Empty;
    public Guid lineup_2_id { get; set; } = Guid.Empty;

    public MatchLineUp lineup_1 { get; set; } = new MatchLineUp();
    public MatchLineUp lineup_2 { get; set; } = new MatchLineUp();
}
