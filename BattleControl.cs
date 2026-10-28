// The two big buttons on the page (Attack, Recall) and the live status line.
// One instance is shared by the web routes and whichever bot is playing.
public class BattleControl
{
    private int _attack;
    private int _recall;

    public void RequestAttack() => Interlocked.Exchange(ref _attack, 1);
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
    int WaveSize);
