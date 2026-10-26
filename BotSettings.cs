public record BotSettings
{
    // Economy
    public int TargetWorkers { get; init; } = 13;
    public int DepotAtWorkers { get; init; } = 10;            // 0 = never
    public int ScoutOreAtTick { get; init; } = 120;           // 0 = never scout
    public int ExpandDepot { get; init; } = 1;                // 1 = depot at far ore
    public int ExpandWhenHomeOreBelow { get; init; } = 1000;  // ore left beside our depots; 0 = only when empty
    public int LateWorkers { get; init; } = 0;
    public int LateWorkersAtTick { get; init; } = 400;

    // Production
    public int BarracksCount { get; init; } = 2;              // planned by worker count
    public int FirstBarracksAt { get; init; } = 8;
    public int ExtraBarracksAt { get; init; } = 12;
    public int ArcherPercent { get; init; } = 33;
    public int BankBarracksAt { get; init; } = 500;           // bonus Barracks when ore >= this; 0 = off
    public int MaxBarracks { get; init; } = 4;

    // Tech: Harvest1, Weapons1, Armor1, Weapons2, Armor2 in that order
    public int UpgradeCount { get; init; } = 5;
    public int TechLabAt { get; init; } = 11;

    // Defense
    public int TowerCount { get; init; } = 2;
    public int TowerAt { get; init; } = 13;
    public int FarTower { get; init; } = 1;                   // tower beside the far depot
    public int DefendPercent { get; init; } = 16;             // garrison share that never leaves home

    // Offense
    public int AttackAtTick { get; init; } = 1200;            // 0 = launch by army size only
    public int AttackThreshold { get; init; } = 12;           // minimum troops to launch
    public int RaiderScout { get; init; } = 1;                // 1 = rogue scout hunts workers

    public BotSettings Clamped() => this with
    {
        TargetWorkers = Math.Clamp(TargetWorkers, 5, 20),
        DepotAtWorkers = Math.Clamp(DepotAtWorkers, 0, 18),
        ScoutOreAtTick = Math.Clamp(ScoutOreAtTick, 0, 400),
        ExpandDepot = Math.Clamp(ExpandDepot, 0, 1),
        ExpandWhenHomeOreBelow = Math.Clamp(ExpandWhenHomeOreBelow, 0, 3000),
        LateWorkers = Math.Clamp(LateWorkers, 0, 10),
        LateWorkersAtTick = Math.Clamp(LateWorkersAtTick, 100, 900),
        BarracksCount = Math.Clamp(BarracksCount, 1, 4),
        FirstBarracksAt = Math.Clamp(FirstBarracksAt, 4, 12),
        ExtraBarracksAt = Math.Clamp(ExtraBarracksAt, 6, 18),
        ArcherPercent = Math.Clamp(ArcherPercent, 0, 100),
        BankBarracksAt = Math.Clamp(BankBarracksAt, 0, 1000),
        MaxBarracks = Math.Clamp(MaxBarracks, 1, 6),
        UpgradeCount = Math.Clamp(UpgradeCount, 0, 5),
        TechLabAt = Math.Clamp(TechLabAt, 6, 16),
        TowerCount = Math.Clamp(TowerCount, 0, 6),
        TowerAt = Math.Clamp(TowerAt, 8, 18),
        FarTower = Math.Clamp(FarTower, 0, 1),
        DefendPercent = Math.Clamp(DefendPercent, 0, 100),
        AttackAtTick = Math.Clamp(AttackAtTick, 0, 1800),
        AttackThreshold = Math.Clamp(AttackThreshold, 1, 100),
        RaiderScout = Math.Clamp(RaiderScout, 0, 1),
    };
}

public class SettingsStore
{
    private volatile BotSettings _current = new();
    public BotSettings Current => _current;
    public bool Locked { get; private set; }

    public void Lock()
    {
        Locked = true;
        Console.WriteLine("Settings locked for the match.");
    }

    public void Update(BotSettings s)
    {
        if (!Locked)
        {
            _current = s;
        }
    }
}