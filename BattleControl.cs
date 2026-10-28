// The two big buttons on the page (Attack, Recall) and the live status line.
// One instance is shared by the web routes and whichever bot is playing.
public class BattleControl
{
    private int _attack;
    private int _recall;
    private string? _direction;
    private int _raidFlag;

    // Which copy of the app this is, and which match its bot is playing (shown on the page to spot mix-ups).
    public int Pid { get; } = Environment.ProcessId;
    public DateTime StartedAt { get; } = DateTime.Now;
    public volatile string MatchId = "";

    // direction: "left", "center", "right" or null (nearest enemy)
    public void RequestAttack(string? direction = null, bool raid = false)
    {
        Volatile.Write(ref _direction, direction);
        Interlocked.Exchange(ref _raidFlag, raid ? 1 : 0);
        Interlocked.Exchange(ref _attack, 1);
    }

    public bool TakeRaid() => Interlocked.Exchange(ref _raidFlag, 0) == 1;

    public string? TakeDirection() => Interlocked.Exchange(ref _direction, null);
    public void RequestRecall() => Interlocked.Exchange(ref _recall, 1);

    // The bot calls these once per tick; each returns true once per button press.
    public bool TakeAttack() => Interlocked.Exchange(ref _attack, 0) == 1;
    public bool TakeRecall() => Interlocked.Exchange(ref _recall, 0) == 1;

    public volatile BattleStatus? Status;
    public SerumControl Serum { get; } = new();
}

public record BattleStatus(
    int Tick,
    int Workers,
    int Fighters,
    int Trebuchets,
    int Ore,
    int IncomePerMinute,
    int Upgrades,
    bool WaveOut,
    int WaveSize,
    string Targets,
    int Raiders);
