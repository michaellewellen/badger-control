using NetRts.Bots;
using NetRts.Protocol;

public class TunableBot(SettingsStore store, BattleControl control) : IBotStrategy
{
    private const int DefenseRadius = 10;
    private const int RetargetEveryTicks = 15;
    private const int EscortEveryTicks = 3;
    private const int HomeRadius = 12;     // buildings this close to the Command Center are "home"
    private const int TierOneCount = 3;    // Harvesting1, Weapons1, Armor1
    private const int TierTwoIndex = 4;    // Weapons2 and Armor2 (positions 4 and 5) wait for Barracks #3
    private const int MinArmyBeforeSiege = 5;   // soldiers/archers we want before spending 350 on a trebuchet

    // Research order. The UpgradeCount slider = how many of these we buy.
    private static readonly UpgradeType[] UpgradeOrder =
    [
        UpgradeType.Harvesting1, UpgradeType.Weapons1, UpgradeType.Armor1,
        UpgradeType.SiegeEngineering,
        UpgradeType.Weapons2, UpgradeType.Armor2,
    ];

    // Where escorts stand around the trebuchets when a wave rolls out: neighbours two tiles out, then three.
    private static readonly (int X, int Y)[] EscortRing =
    [
        (2, 0), (-2, 0), (0, 2), (0, -2), (2, 2), (-2, -2), (2, -2), (-2, 2),
        (3, 0), (-3, 0), (0, 3), (0, -3), (3, 3), (-3, -3), (3, -3), (-3, 3),
    ];

    // Offsets from our base toward the map centre (sign-adjusted per corner).
    private static readonly (int X, int Y)[] TourOffsets = [(16, 16), (24, 10), (10, 24), (12, 12)];
    private static readonly (int X, int Y)[] SearchOffsets = [(16, 16), (12, 12), (20, 20), (24, 10), (10, 24), (28, 28)];

    // Eight points on a ring around an enemy base; the raider scout walks them.
    private static readonly (int X, int Y)[] Ring =
        [(10, 0), (10, 10), (0, 10), (-10, 10), (-10, 0), (-10, -10), (0, -10), (10, -10)];

    private readonly Dictionary<(int X, int Y), (BuildingType Type, int Tick)> _pendingSites = [];
    private readonly Dictionary<int, (int X, int Y)> _knownDeposits = [];
    private readonly Dictionary<BuildingType, int> _peak = [];   // most of each home building type we've had
    private HashSet<int> _wave = [];          // soldiers and archers that have been sent out
    private HashSet<int> _siegeWave = [];     // our trebuchets that have been sent out
    private int _waveSize;
    private readonly HashSet<int> _raid = [];   // soldiers on a worker raid
    private int _raidSlot = -1;
    private int _raidLastWorkerTick;   // last time the raid saw (and so fought) an enemy worker
    private int? _targetSlot;   // the enemy chosen with Attack left/center/right; null = nearest

    // Escort that travels with the workers to a new site
    private HashSet<int> _escortIds = [];
    private int _escortSinceTick = -1000;
    private int _escortWaitSince = -1;

    // Anti-siege
    private int _lastHuntNote = -1000;
    private int _lastDodgeNote = -1000;
    private int _lastRetargetTick = -RetargetEveryTicks;
    private int _lastTick = -1;
    private int _lastLoggedEventTick = -1;
    private int _explorerId = -1;
    private int _tourStep;
    private (int X, int Y)[] _tourOffsets = TourOffsets;
    private int _lateTours;
    private int _lastLateTour = -1000;
    private int _waypoint;
    private int _expansionOrderTick = -1000;
    private (int X, int Y)? _escortSite;   // where the current expedition (builder + army) is headed
    private (int X, int Y)? _farTowerSite;
    private int _farTowerSiteTick;
    private int _farBuilderId = -1;
    private (int X, int Y)? _farSite;      // the far depot we've ordered but not yet seen standing
    private int _farSiteTick;
    private int _farTowerOrderTick = -1000;
    private int _lastDepotNote = -1000;
    private int _lastRetreatNote = -1000;
    private bool _saving;   // true = something important is waiting for ore, so lower priorities must not spend

    // Rogue scout
    private int _raiderId = -1;
    private int _raidersBuilt;
    private int _raiderQueuedTick = -1000;
    private int _raiderBase;
    private int _raiderPoint;
    private int _raiderFleeUntil = -1;
    private int _raiderTargetId = -1;

    public string Name => "tunable";
    public int SkippedTicks { get; private set; }

    public IReadOnlyList<CommandRequest> Decide(BotContext context)
    {
        var view = new BotView(context);
        if (view.State.Status != MatchStatus.Active || view.CommandCenter is null)
        {
            return [];
        }

        var tick = view.State.Tick;
        if (_lastTick >= 0 && tick > _lastTick + 1)
        {
            SkippedTicks += tick - _lastTick - 1;
        }
        _lastTick = tick;

        var s = store.Current;   // read once so a slider can't change mid-tick
        var commands = new List<CommandRequest>();
        var budget = view.State.You.Resources;
        _saving = false;

        var reserved = 0;
        // A Build order only charges ore when the builder arrives, so hold that ore back meanwhile
        // or the army spends it first and the building never starts.
        foreach (var kv in _pendingSites)
        {
            if (view.MyBuildings.Any(b => b.X == kv.Key.X && b.Y == kv.Key.Y)) { continue; }
            reserved += view.Rules.Buildings.First(b => b.Type == kv.Value.Type).Cost;
        }

        if (_farTowerSite is { } ft)
        {
            if (view.MyBuildings.Any(b => b.X == ft.X && b.Y == ft.Y) || tick - _farTowerSiteTick > 150)
            {
                _farTowerSite = null;
            }
            else
            {
                reserved += view.Rules.Buildings.First(b => b.Type == BuildingType.GuardTower).Cost;
            }
        }

        if (_farSite is { } fs)
        {
            var started = view.MyBuildings.Any(b => b.X == fs.X && b.Y == fs.Y);
            if (!started && !view.Workers.Any(w => w.Id == _farBuilderId))
            {
                // The builder died on the way: try again soon instead of waiting out the timer.
                Console.WriteLine($"[tick {tick}] the depot builder was lost before the depot started: sending another");
                _farSite = null;
                _expansionOrderTick = tick - 40;
            }
            else if (started || tick - _farSiteTick > 150)
            {
                _farSite = null;
            }
            else
            {
                reserved += view.Rules.Buildings.First(b => b.Type == BuildingType.ResourceDepot).Cost;
            }
        }

        LogEvents(view);
        if (tick % 100 == 0)
        {
            var cc = view.CommandCenter;
            var homeOre = view.State.Resources.Where(d => Dist(d.X, d.Y, cc.X, cc.Y) <= 9).Sum(d => d.Remaining);
            Console.WriteLine($"[tick {tick}] workers {view.Workers.Count}, army {view.Army.Count}, ore {view.State.You.Resources}, " +
                              $"income/min {view.State.You.IncomePerMinute}, upgrades {view.State.You.Upgrades.Count}, home ore left {homeOre}");
        }

        PublishStatus(view);   // early on purpose: the page shows the bot is alive even if a later step fails
        RememberDeposits(view);

        // Priority order. Each step only spends if nothing above it is waiting for ore.
        TrainWorkers(view, s, commands, ref budget);   // workers are the lifeline: they may use ore held for buildings
        budget -= reserved;
        FollowBuildOrder(view, s, commands, ref budget);   // lost buildings, the TechLab and the workshop save first
        FinishAbandonedSites(view, commands);
        if (!_saving) { ExpandDepot(view, s, commands, ref budget); }
        if (!_saving) { BuildFarTower(view, s, commands, ref budget); }
        if (!_saving) { Research(view, s, commands, ref budget); }
        if (!_saving) { BankBarracks(view, s, commands, ref budget); }
        if (!_saving) { MaybeBuildRaider(view, s, commands, ref budget); }
        if (!_saving) { TrainArmy(view, s, commands, ref budget); }

        if (_saving && tick % 25 == 0)
        {
            Console.WriteLine($"[tick {tick}] saving ore for a priority item (have {budget})");
        }

        CommandArmy(view, s, commands);
        HuntEnemySiege(view, commands);
        RunRaider(view, s, commands);
        RetreatWorkers(view, commands);
        PutIdleWorkersToWork(view, commands);
        ScoutTour(view, s, commands);
        DodgeShells(view, commands);   // last on purpose: a later command overrides an earlier one

        PublishStatus(view);
        return commands;
    }

    // What the page shows next to the Attack button.
    private void PublishStatus(BotView view)
    {
        var you = view.State.You;
        control.Status = new BattleStatus(
            Tick: view.State.Tick,
            Workers: view.Workers.Count,
            Fighters: view.Army.Count(u => u.Type is UnitType.Soldier or UnitType.Archer),
            Trebuchets: view.Siege.Count,
            Ore: you.Resources,
            IncomePerMinute: you.IncomePerMinute,
            Upgrades: you.Upgrades.Count,
            WaveOut: _wave.Count + _siegeWave.Count > 0,
            WaveSize: _wave.Count + _siegeWave.Count,
            Targets: DescribeSides(view),
            Raiders: _raid.Count);
    }

    // ---- Logging ----

    private void LogEvents(BotView view)
    {
        foreach (var e in view.State.Events.Where(e => e.Tick > _lastLoggedEventTick))
        {
            if (e.Kind is "UnitLost" or "BuildingLost" or "UnitKilled" or "BuildingDestroyed"
                or "CommandFailed" or "DepositDepleted" or "ResearchCompleted")
            {
                Console.WriteLine($"[tick {e.Tick}] {e.Kind}: {e.Message}");
            }
        }

        if (view.State.Events.Count > 0)
        {
            _lastLoggedEventTick = Math.Max(_lastLoggedEventTick, view.State.Events.Max(e => e.Tick));
        }
    }

    // ---- Workers ----

    private static void TrainWorkers(BotView view, BotSettings s, List<CommandRequest> commands, ref int budget)
    {
        var target = s.TargetWorkers + (view.State.Tick >= s.LateWorkersAtTick ? s.LateWorkers : 0);
        var cost = view.UnitCost(UnitType.Worker);
        foreach (var cc in view.MyBuildings.Where(b => b.Type == BuildingType.CommandCenter && b.Completed))
        {
            var queued = view.MyBuildings.Sum(b => b.Production.Count(p => p.UnitType == UnitType.Worker));
            if (view.Workers.Count + queued >= target || cc.Production.Count >= 2 || budget < cost)
            {
                continue;
            }

            commands.Add(new CommandRequest { Type = CommandType.Produce, BuildingId = cc.Id, UnitType = UnitType.Worker });
            budget -= cost;
        }
    }

    // Deposits are only listed while one of our units can see them, so remember every one we've seen.
    private void RememberDeposits(BotView view)
    {
        var seenNow = view.State.Resources.Select(d => d.Id).ToHashSet();
        foreach (var d in view.State.Resources)
        {
            _knownDeposits[d.Id] = (d.X, d.Y);
        }

        // If we can see a remembered deposit's tile right now and it isn't listed, it's mined out.
        foreach (var (id, pos) in _knownDeposits.ToList())
        {
            var visible = pos.Y < view.State.Visibility.Count
                          && pos.X < view.State.Visibility[pos.Y].Length
                          && view.State.Visibility[pos.Y][pos.X] == '1';
            if (!seenNow.Contains(id) && visible)
            {
                _knownDeposits.Remove(id);
            }
        }
    }

    // Only deposits closer to our base than to the enemy's: workers must never walk into their camp.
    private List<KeyValuePair<int, (int X, int Y)>> SafeDeposits(BotView view)
    {
        var cc = view.CommandCenter!;
        var enemy = view.EnemyStart(cc.X, cc.Y);
        return _knownDeposits
            .Where(kv => enemy is null
                         || Dist(kv.Value.X, kv.Value.Y, cc.X, cc.Y) < Dist(kv.Value.X, kv.Value.Y, enemy.X, enemy.Y))
            .ToList();
    }

    // Distance from a point to our nearest finished drop-off (Command Center or depot).
    private static int DropOffDist(BotView view, (int X, int Y) p) =>
        view.MyBuildings
            .Where(b => b.Completed && (b.Type == BuildingType.CommandCenter || b.Type == BuildingType.ResourceDepot))
            .Select(b => Dist(b.X, b.Y, p.X, p.Y))
            .DefaultIfEmpty(0)
            .Min();

    // A far-field spot is dangerous when enemy fighters near it outnumber our own. Home ore never counts.
    private static bool Threatened(BotView view, int x, int y)
    {
        var cc = view.CommandCenter!;
        if (Dist(x, y, cc.X, cc.Y) <= 9)
        {
            return false;
        }

        var enemies = view.EnemyUnits.Count(e => e.Type != UnitType.Worker && Dist(e.X, e.Y, x, y) <= 8);
        if (enemies == 0)
        {
            return false;
        }

        var friends = view.Army.Count(u => u.Type != UnitType.Scout && Dist(u.X, u.Y, x, y) <= 8);
        return enemies > friends;
    }

    // Workers standing in a spot that has become dangerous walk back behind the Command Center.
    private void RetreatWorkers(BotView view, List<CommandRequest> commands)
    {
        var scared = view.Workers.Where(w => Threatened(view, w.X, w.Y)).Select(w => w.Id).ToArray();
        if (scared.Length == 0)
        {
            return;
        }

        var cc = view.CommandCenter!;
        var sx = Math.Sign(view.Map.Width / 2 - cc.X);
        var sy = Math.Sign(view.Map.Height / 2 - cc.Y);
        var spot = OpenNear(view, cc.X - sx * 2, cc.Y - sy * 2);

        if (view.State.Tick - _lastRetreatNote >= 20)
        {
            _lastRetreatNote = view.State.Tick;
            Console.WriteLine($"[tick {view.State.Tick}] enemy fighters outnumber ours at the far field: pulling {scared.Length} workers home");
        }

        commands.Add(new CommandRequest { Type = CommandType.Move, UnitIds = scared, X = spot.X, Y = spot.Y });
    }

    private void PutIdleWorkersToWork(BotView view, List<CommandRequest> commands)
    {
        var safe = SafeDeposits(view);
        var cc = view.CommandCenter!;

        // Far ore is only worth walking to once a finished drop-off stands beside it (or if the depot is
        // clearly never coming), otherwise every load is carried all the way back home.
        var depotOverdue = view.State.Tick - _expansionOrderTick > 150;
        var usable = safe
            .Where(kv => !Threatened(view, kv.Value.X, kv.Value.Y))
            .Where(kv => Dist(kv.Value.X, kv.Value.Y, cc.X, cc.Y) <= 9
                         || depotOverdue
                         || view.MyBuildings.Any(b => b.Completed
                                                      && (b.Type == BuildingType.CommandCenter || b.Type == BuildingType.ResourceDepot)
                                                      && Dist(b.X, b.Y, kv.Value.X, kv.Value.Y) <= 8))
            .ToList();
        var assigned = view.Workers
            .Where(w => w.TargetId is not null && w.Activity != UnitActivity.Idle)
            .GroupBy(w => w.TargetId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var worker in view.Workers.Where(w => w.Activity == UnitActivity.Idle))
        {
            if (safe.Count == 0)
            {
                // Nothing safe to mine: send ONE worker looking, keep the rest waiting.
                var explorerBusy = view.Workers.Any(w => w.Id == _explorerId && w.Activity != UnitActivity.Idle);
                if (!explorerBusy)
                {
                    SendExplorer(view, worker, commands);
                }

                return;
            }

            if (usable.Count == 0)
            {
                return;   // ore exists, but enemy fighters are on it: wait at home
            }

            var depositId = usable
                .OrderBy(kv => DropOffDist(view, kv.Value) + 3 * assigned.GetValueOrDefault(kv.Key))
                .ThenBy(kv => kv.Key)
                .First().Key;

            commands.Add(new CommandRequest { Type = CommandType.Gather, UnitIds = [worker.Id], TargetId = depositId });
            assigned[depositId] = assigned.GetValueOrDefault(depositId) + 1;
        }
    }

    // Fallback: the home field is gone and we know no safe ore, so go looking.
    private void SendExplorer(BotView view, UnitDto worker, List<CommandRequest> commands)
    {
        var cc = view.CommandCenter!;
        var sx = Math.Sign(view.Map.Width / 2 - cc.X);
        var sy = Math.Sign(view.Map.Height / 2 - cc.Y);

        for (var tries = 0; tries < SearchOffsets.Length; tries++)
        {
            var (ox, oy) = SearchOffsets[_waypoint++ % SearchOffsets.Length];
            var x = Math.Clamp(cc.X + sx * ox, 1, view.Map.Width - 2);
            var y = Math.Clamp(cc.Y + sy * oy, 1, view.Map.Height - 2);
            if (!view.IsOpen(x, y))
            {
                continue;
            }

            _explorerId = worker.Id;
            Console.WriteLine($"[tick {view.State.Tick}] no safe ore known; worker {worker.Id} exploring to ({x},{y})");
            commands.Add(new CommandRequest { Type = CommandType.Move, UnitIds = [worker.Id], X = x, Y = y });
            return;
        }
    }

    // One worker walks a short loop so we learn where the nearby ore is.
    private void ScoutTour(BotView view, BotSettings s, List<CommandRequest> commands)
    {
        if (s.ScoutOreAtTick == 0 || view.State.Tick < s.ScoutOreAtTick || _tourStep >= _tourOffsets.Length)
        {
            return;
        }

        var cc = view.CommandCenter!;
        var explorer = view.Workers.FirstOrDefault(w => w.Id == _explorerId)
                       ?? view.Workers.Where(w => w.Carrying == 0).OrderBy(w => w.Id).FirstOrDefault();
        if (explorer is null || (explorer.Id == _explorerId && explorer.Activity != UnitActivity.Idle))
        {
            return;   // no worker to spare, or the explorer is still busy (walking or building)
        }

        var sx = Math.Sign(view.Map.Width / 2 - cc.X);
        var sy = Math.Sign(view.Map.Height / 2 - cc.Y);
        var (ox, oy) = _tourOffsets[_tourStep++];
        var x = Math.Clamp(cc.X + sx * ox, 1, view.Map.Width - 2);
        var y = Math.Clamp(cc.Y + sy * oy, 1, view.Map.Height - 2);
        if (!view.IsOpen(x, y))
        {
            return;   // rock: skip this waypoint
        }

        _explorerId = explorer.Id;
        Console.WriteLine($"[tick {view.State.Tick}] scouting for ore: worker {explorer.Id} to ({x},{y})");
        commands.Add(new CommandRequest { Type = CommandType.Move, UnitIds = [explorer.Id], X = x, Y = y });
    }

    // ---- Far-ore depot and tower ----

    // When the ore beside our depots runs low, builds a depot at the nearest uncovered safe field.
    private void ExpandDepot(BotView view, BotSettings s, List<CommandRequest> commands, ref int budget)
    {
        var tick = view.State.Tick;
        if (s.ExpandDepot == 0 || tick < 150 || tick - _expansionOrderTick < 60 || _farSite is not null)
        {
            return;
        }

        var cc = view.CommandCenter!;
        var finishedDrops = view.MyBuildings
            .Where(b => b.Completed && (b.Type == BuildingType.CommandCenter || b.Type == BuildingType.ResourceDepot))
            .ToList();
        var allDrops = view.MyBuildings
            .Where(b => b.Type == BuildingType.CommandCenter || b.Type == BuildingType.ResourceDepot)
            .ToList();

        // Wait until the ore beside our finished drop-offs is nearly gone.
        var oreLeft = view.State.Resources
            .Where(d => finishedDrops.Any(b => Dist(b.X, b.Y, d.X, d.Y) <= 9))
            .Sum(d => d.Remaining);
        if (oreLeft >= Math.Max(1, s.ExpandWhenHomeOreBelow))
        {
            return;
        }

        // Safe, far, calm, and not already covered by a drop-off (finished or under construction).
        var far = SafeDeposits(view)
            .Where(kv => Dist(kv.Value.X, kv.Value.Y, cc.X, cc.Y) > 9)
            .Where(kv => !Threatened(view, kv.Value.X, kv.Value.Y))
            .Where(kv => !allDrops.Any(b => Dist(b.X, b.Y, kv.Value.X, kv.Value.Y) <= 8))
            .OrderBy(kv => Dist(kv.Value.X, kv.Value.Y, cc.X, cc.Y))
            .Select(kv => kv.Value)
            .ToList();

        if (far.Count == 0)
        {
            // Nothing new known: send the explorer on a wider loop (at most twice).
            if (tick > 300 && _lateTours < 2 && tick - _lastLateTour > 200 && _tourStep >= _tourOffsets.Length)
            {
                _tourOffsets = SearchOffsets;
                _tourStep = 0;
                _lateTours++;
                _lastLateTour = tick;
                Console.WriteLine($"[tick {tick}] ore is running low and no new field is known: scouting wider");
            }

            return;
        }

        // The field = the nearest far deposit plus its neighbours.
        var seed = far[0];
        var cluster = far.Where(p => Dist(p.X, p.Y, seed.X, seed.Y) <= 4).ToList();
        var cx = (int)Math.Round(cluster.Average(p => p.X));
        var cy = (int)Math.Round(cluster.Average(p => p.Y));

        var cost = view.Rules.Buildings.First(b => b.Type == BuildingType.ResourceDepot).Cost;
        if (budget < cost)
        {
            _saving = true;   // everything below waits until we can pay for the depot
            if (tick - _lastDepotNote >= 50)
            {
                _lastDepotNote = tick;
                Console.WriteLine($"[tick {tick}] saving ore for the far depot (have {budget}, need {cost})");
            }

            return;
        }

        var site = FindSiteNear(view, cx, cy);
        var builder = site is null ? null : view.PickBuilder(site.Value);
        if (site is null || builder is null)
        {
            return;
        }

        // The builder doesn't go alone: a few soldiers leave with it (same speed, so they arrive together).
        var escort = PickEscort(view, s, site.Value);
        if (escort.Count < s.EscortSize)
        {
            _escortWaitSince = _escortWaitSince < 0 ? tick : _escortWaitSince;
            if (tick - _escortWaitSince < 120)
            {
                if (tick % 20 == 0)
                {
                    Console.WriteLine($"[tick {tick}] far depot is waiting for an escort ({escort.Count} of {s.EscortSize} soldiers ready)");
                }

                return;   // wait for the escort, but not forever
            }
        }

        Console.WriteLine($"[tick {tick}] building a depot at far ore near ({cx},{cy}), site {site}, builder {builder.Id}, escort {escort.Count}");
        commands.Add(new CommandRequest
        {
            Type = CommandType.Build,
            UnitIds = [builder.Id],
            BuildingType = BuildingType.ResourceDepot,
            X = site.Value.X,
            Y = site.Value.Y,
        });

        if (escort.Count > 0)
        {
            _escortIds = escort.Select(u => u.Id).ToHashSet();
            _escortSinceTick = tick;
            _escortSite = site.Value;
            commands.Add(new CommandRequest
            {
                Type = CommandType.Attack,
                UnitIds = escort.Select(u => u.Id).ToArray(),
                X = site.Value.X,
                Y = site.Value.Y,
            });
        }

        _escortWaitSince = -1;
        _expansionOrderTick = tick;
        _farSite = site.Value;
        _farSiteTick = tick;
        _farBuilderId = builder.Id;
        budget -= cost;
    }

    // Everyone at the current field travels with the builder to the next one; short of EscortSize, the closest others join.
    private List<UnitDto> PickEscort(BotView view, BotSettings s, (int X, int Y) site)
    {
        if (s.EscortSize == 0)
        {
            return [];
        }

        var candidates = view.Army
            .Where(u => u.Type is UnitType.Soldier or UnitType.Archer && !_wave.Contains(u.Id))
            .ToList();
        var old = FarRally(view);
        var field = old is null
            ? []
            : candidates.Where(u => Dist(u.X, u.Y, old.Value.X, old.Value.Y) <= 12).ToList();
        var rest = candidates
            .Where(u => !field.Contains(u))
            .OrderBy(u => u.Type == UnitType.Soldier ? 0 : 1)
            .ThenBy(u => Dist(u.X, u.Y, site.X, site.Y))
            .Take(Math.Max(0, s.EscortSize - field.Count));
        return field.Concat(rest).ToList();
    }

    // Once the far depot is finished, one Guard Tower goes up beside it, on the enemy side.
    private void BuildFarTower(BotView view, BotSettings s, List<CommandRequest> commands, ref int budget)
    {
        var tick = view.State.Tick;
        if (s.FarTower == 0 || tick - _farTowerOrderTick < 30 || _farTowerSite is not null)
        {
            return;
        }

        var cc = view.CommandCenter!;
        // Every far depot (even one still going up) gets its own tower: the first depot without one.
        var depot = view.MyBuildings
            .Where(b => b.Type == BuildingType.ResourceDepot && Dist(b.X, b.Y, cc.X, cc.Y) > HomeRadius)
            .Where(d => !view.MyBuildings.Any(t => t.Type == BuildingType.GuardTower && Dist(t.X, t.Y, d.X, d.Y) <= 5))
            .OrderBy(b => b.Id)
            .FirstOrDefault();
        if (depot is null)
        {
            return;
        }

        var cost = view.Rules.Buildings.First(b => b.Type == BuildingType.GuardTower).Cost;
        if (budget < cost)
        {
            _saving = true;
            return;
        }

        var enemy = view.EnemyStart(cc.X, cc.Y);
        var ax = depot.X + (enemy is null ? 0 : Math.Sign(enemy.X - depot.X)) * 2;
        var ay = depot.Y + (enemy is null ? 0 : Math.Sign(enemy.Y - depot.Y)) * 2;
        var site = FindSiteNear(view, ax, ay);
        var builder = site is null ? null : view.PickBuilder(site.Value);
        if (site is null || builder is null)
        {
            return;
        }

        Console.WriteLine($"[tick {tick}] building a tower at the far depot, site {site}, builder {builder.Id}");
        commands.Add(new CommandRequest
        {
            Type = CommandType.Build,
            UnitIds = [builder.Id],
            BuildingType = BuildingType.GuardTower,
            X = site.Value.X,
            Y = site.Value.Y,
        });
        _farTowerOrderTick = tick;
        _farTowerSite = site.Value;
        _farTowerSiteTick = tick;
        budget -= cost;
    }

    // An open tile close to (cx, cy) that doesn't sit on or beside ore or other buildings.
    private (int X, int Y)? FindSiteNear(BotView view, int cx, int cy)
    {
        (int X, int Y)? best = null;
        var bestScore = int.MaxValue;
        for (var y = cy - 5; y <= cy + 5; y++)
        {
            for (var x = cx - 5; x <= cx + 5; x++)
            {
                if (!view.IsOpen(x, y)
                    || view.State.Buildings.Any(b => Dist(b.X, b.Y, x, y) <= 1)
                    || _knownDeposits.Values.Any(d => Dist(d.X, d.Y, x, y) <= 1))
                {
                    continue;
                }

                var score = Dist(x, y, cx, cy);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = (x, y);
                }
            }
        }

        return best;
    }

    // ---- Bonus Barracks ----

    // When the bank is piling up, turn it into production: one extra Barracks at a time, up to the cap.
    // Not until the first three upgrades are done.
    private void BankBarracks(BotView view, BotSettings s, List<CommandRequest> commands, ref int budget)
    {
        var cost = view.Rules.Buildings.First(b => b.Type == BuildingType.Barracks).Cost;
        if (s.BankBarracksAt == 0 || budget < s.BankBarracksAt || budget < cost)
        {
            return;
        }

        var you = view.State.You;
        if (UpgradeOrder.Take(Math.Min(TierOneCount, s.UpgradeCount)).Any(u => !you.Upgrades.Contains(u)))
        {
            return;
        }

        var count = view.MyBuildings.Count(b => b.Type == BuildingType.Barracks) + PendingCount(view, BuildingType.Barracks);
        var stillBuilding = view.MyBuildings.Any(b => b.Type == BuildingType.Barracks && !b.Completed);
        if (count < s.BarracksCount || count >= s.MaxBarracks || stillBuilding)
        {
            return;   // finish the normal plan first, respect the cap, one at a time
        }

        var site = FindHomeSite(view, SitePreference.TowardCenter, BuildingType.Barracks);
        var builder = site is null ? null : view.PickBuilder(site.Value);
        if (site is null || builder is null)
        {
            return;
        }

        Console.WriteLine($"[tick {view.State.Tick}] bank is {budget}: building Barracks #{count + 1}");
        commands.Add(new CommandRequest
        {
            Type = CommandType.Build,
            UnitIds = [builder.Id],
            BuildingType = BuildingType.Barracks,
            X = site.Value.X,
            Y = site.Value.Y,
        });
        _pendingSites[site.Value] = (BuildingType.Barracks, view.State.Tick);
        budget -= cost;
    }

    // Home layout: the towers form an arc in front of the Command Center (toward the nearest enemy);
    // every other building goes behind or beside it, out of the line of fire.
    private (int X, int Y)? FindHomeSite(BotView view, SitePreference pref, BuildingType type)
    {
        var cc = view.CommandCenter!;
        var enemy = view.EnemyStart(cc.X, cc.Y);
        if (pref == SitePreference.NearOre || enemy is null)
        {
            return view.FindBuildSite(pref, _pendingSites.Keys);
        }

        double dx = enemy.X - cc.X, dy = enemy.Y - cc.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1)
        {
            return view.FindBuildSite(pref, _pendingSites.Keys);
        }

        dx /= len;
        dy /= len;
        var isTower = type == BuildingType.GuardTower;
        double ax, ay;
        if (isTower)
        {
            var n = view.MyBuildings.Count(b => b.Type == BuildingType.GuardTower && Dist(b.X, b.Y, cc.X, cc.Y) <= HomeRadius)
                    + PendingCount(view, BuildingType.GuardTower);
            double[] angles = [0, -0.8, 0.8, -1.4, 1.4, 0];
            var a = angles[n % angles.Length];
            var rx = dx * Math.Cos(a) - dy * Math.Sin(a);
            var ry = dx * Math.Sin(a) + dy * Math.Cos(a);
            ax = cc.X + rx * 7;
            ay = cc.Y + ry * 7;
        }
        else
        {
            ax = cc.X - dx * 5;
            ay = cc.Y - dy * 5;
        }

        (int X, int Y)? best = null;
        var bestScore = double.MaxValue;
        for (var y = (int)ay - 6; y <= (int)ay + 6; y++)
        {
            for (var x = (int)ax - 6; x <= (int)ax + 6; x++)
            {
                if (!view.IsOpen(x, y) || Dist(x, y, cc.X, cc.Y) < 3 || Dist(x, y, cc.X, cc.Y) > 10)
                {
                    continue;
                }

                // behind or level with the Command Center for everything except towers
                var forward = (x - cc.X) * dx + (y - cc.Y) * dy;
                if (!isTower && forward > 0.5)
                {
                    continue;
                }

                if (view.State.Buildings.Any(b => Dist(b.X, b.Y, x, y) <= 1)
                    || _pendingSites.Keys.Any(r => Dist(r.X, r.Y, x, y) <= 1)
                    || view.State.Resources.Any(r => Dist(r.X, r.Y, x, y) <= 2))
                {
                    continue;
                }

                var score = Math.Sqrt((x - ax) * (x - ax) + (y - ay) * (y - ay));
                if (score < bestScore)
                {
                    bestScore = score;
                    best = (x, y);
                }
            }
        }

        return best ?? view.FindBuildSite(pref, _pendingSites.Keys);
    }

    // ---- Build order: the sliders become a list, sorted by "at N workers" ----

    private static List<BuildStep> MakePlan(BotSettings s)
    {
        var steps = new List<BuildStep> { new(BuildingType.Barracks, 1, s.FirstBarracksAt) };

        if (s.DepotAtWorkers > 0)
        {
            steps.Add(new(BuildingType.ResourceDepot, 1, s.DepotAtWorkers, SitePreference.NearOre));
        }

        if (s.UpgradeCount > 0)
        {
            steps.Add(new(BuildingType.TechLab, 1, s.TechLabAt));
        }

        // The workshop needs the TechLab and the SiegeEngineering upgrade; until both exist the step is skipped.
        if (s.TrebuchetPercent > 0 && s.UpgradeCount > Array.IndexOf(UpgradeOrder, UpgradeType.SiegeEngineering))
        {
            steps.Add(new(BuildingType.SiegeWorkshop, 1, s.WorkshopAt));
        }

        for (var n = 2; n <= s.BarracksCount; n++)
        {
            steps.Add(new(BuildingType.Barracks, n, s.ExtraBarracksAt));
        }

        for (var n = 1; n <= s.TowerCount; n++)
        {
            steps.Add(new(BuildingType.GuardTower, n, s.TowerAt, SitePreference.TowardEnemy));
        }

        // OrderBy is stable, so ties keep the order they were added above.
        return steps.OrderBy(x => x.MinWorkers).ToList();
    }

    private void FollowBuildOrder(BotView view, BotSettings s, List<CommandRequest> commands, ref int budget)
    {
        foreach (var site in _pendingSites.Where(kv => view.State.Tick - kv.Value.Tick > 90).Select(kv => kv.Key).ToList())
        {
            _pendingSites.Remove(site);
        }

        var cc = view.CommandCenter!;
        var homeBuildings = view.MyBuildings.Where(b => Dist(b.X, b.Y, cc.X, cc.Y) <= HomeRadius).ToList();

        // Remember the most of each type we've had at home, so we can tell "rebuild" from "not built yet".
        foreach (var g in homeBuildings.GroupBy(b => b.Type))
        {
            _peak[g.Key] = Math.Max(_peak.GetValueOrDefault(g.Key), g.Count());
        }

        var homeOreSeen = view.State.Resources.Any(r => Dist(r.X, r.Y, cc.X, cc.Y) <= 10);

        foreach (var step in MakePlan(s))
        {
            if (step.Type == BuildingType.ResourceDepot && !homeOreSeen)
            {
                continue;   // no home ore left to serve
            }

            var have = homeBuildings.Count(b => b.Type == step.Type) + PendingCount(view, step.Type);
            if (have >= step.Count)
            {
                continue;
            }

            if (step.Type == BuildingType.GuardTower && step.Count > 3 && view.State.Tick < s.LateTowerAtTick)
            {
                continue;   // towers 4 and later come once the early army is built
            }

            var stats = view.Rules.Buildings.First(b => b.Type == step.Type);
            var prerequisitesMet = stats.Requires.All(r => view.MyBuildings.Any(b => b.Type == r && b.Completed))
                                   && (stats.RequiresUpgrade is not { } needed || view.State.You.Upgrades.Contains(needed));
            if (!prerequisitesMet)
            {
                continue;   // e.g. a tower before its Barracks is done: skip it, let later steps run
            }

            if (view.Workers.Count < step.MinWorkers)
            {
                return;     // not enough workers yet
            }

            if (budget < stats.Cost)
            {
                // Replacing something we lost, or the TechLab / siege workshop: the army waits until we can pay.
                if (_peak.GetValueOrDefault(step.Type) >= step.Count
                    || step.Type is BuildingType.TechLab or BuildingType.SiegeWorkshop)
                {
                    _saving = true;
                }

                return;
            }

            var site = FindHomeSite(view, step.Site, step.Type);
            var builder = site is null ? null : view.PickBuilder(site.Value);
            if (site is null || builder is null)
            {
                return;
            }

            commands.Add(new CommandRequest
            {
                Type = CommandType.Build,
                UnitIds = [builder.Id],
                BuildingType = step.Type,
                X = site.Value.X,
                Y = site.Value.Y,
            });
            _pendingSites[site.Value] = (step.Type, view.State.Tick);
            budget -= stats.Cost;
            return;
        }
    }

    private int PendingCount(BotView view, BuildingType type) =>
        _pendingSites.Count(kv => kv.Value.Type == type
                                  && !view.MyBuildings.Any(b => b.X == kv.Key.X && b.Y == kv.Key.Y));

    private static void FinishAbandonedSites(BotView view, List<CommandRequest> commands)
    {
        foreach (var site in view.MyBuildings.Where(b => !b.Completed))
        {
            if (view.Workers.Any(w => w.TargetId == site.Id || (w.Destination is { } d && d.X == site.X && d.Y == site.Y)))
            {
                continue;
            }

            var builder = view.PickBuilder((site.X, site.Y));
            if (builder is not null)
            {
                commands.Add(new CommandRequest
                {
                    Type = CommandType.Build,
                    UnitIds = [builder.Id],
                    BuildingType = site.Type,
                    X = site.X,
                    Y = site.Y,
                });
            }
        }
    }

    // ---- Upgrades: Harvest1, Weapons1, Armor1, SiegeEngineering, then (after Barracks #3) Weapons2, Armor2 ----

    private void Research(BotView view, BotSettings s, List<CommandRequest> commands, ref int budget)
    {
        var lab = view.MyBuildings.FirstOrDefault(b => b.Type == BuildingType.TechLab && b.Completed && b.Research is null);
        if (lab is null)
        {
            return;
        }

        var you = view.State.You;
        var barracksDone = view.MyBuildings.Count(b => b.Type == BuildingType.Barracks && b.Completed);

        for (var i = 0; i < s.UpgradeCount && i < UpgradeOrder.Length; i++)
        {
            var upgrade = UpgradeOrder[i];
            if (you.Upgrades.Contains(upgrade) || you.Researching.Any(r => r.Upgrade == upgrade))
            {
                continue;
            }

            if (upgrade == UpgradeType.SiegeEngineering && s.TrebuchetPercent == 0)
            {
                continue;   // no trebuchets wanted, so no workshop tech
            }

            if (i >= TierTwoIndex && barracksDone < Math.Min(3, s.MaxBarracks))
            {
                return;   // Weapons2 and Armor2 wait for the third Barracks
            }

            var stats = view.Rules.Upgrades.First(u => u.Type == upgrade);
            if (stats.Requires is { } req && !you.Upgrades.Contains(req))
            {
                continue;
            }

            if (budget >= stats.Cost)
            {
                Console.WriteLine($"[tick {view.State.Tick}] researching {upgrade}");
                commands.Add(new CommandRequest { Type = CommandType.Research, BuildingId = lab.Id, Upgrade = upgrade });
                budget -= stats.Cost;
            }
            else
            {
                _saving = true;   // the army waits until the next upgrade is paid for
            }

            return;
        }
    }

    // ---- Army ----

    // The army follows a cycle of soldier, soldier, soldier, archer, trebuchet (3:1:1 with the default sliders):
    // each building makes whichever of its units is furthest behind its share of the army.
    private void TrainArmy(BotView view, BotSettings s, List<CommandRequest> commands, ref int budget)
    {
        var fighters = view.Army.Where(u => u.Type is UnitType.Soldier or UnitType.Archer or UnitType.Trebuchet).ToList();
        var queuedFighters = view.MyBuildings.SelectMany(b => b.Production).ToList();
        var archers = fighters.Count(u => u.Type == UnitType.Archer) + queuedFighters.Count(p => p.UnitType == UnitType.Archer);
        var siege = fighters.Count(u => u.Type == UnitType.Trebuchet) + queuedFighters.Count(p => p.UnitType == UnitType.Trebuchet);
        var total = fighters.Count
                    + queuedFighters.Count(p => p.UnitType is UnitType.Soldier or UnitType.Archer or UnitType.Trebuchet);
        var rally = HomeRally(view);

        // Trebuchets come from the workshop, once there is a small army to protect them.
        var workshop = view.MyBuildings.FirstOrDefault(b => b.Type == BuildingType.SiegeWorkshop && b.Completed && b.Production.Count == 0);
        if (workshop is not null
            && s.TrebuchetPercent > 0
            && total - siege >= MinArmyBeforeSiege
            && siege < Math.Ceiling(s.TrebuchetPercent * (total + 1) / 100.0))
        {
            var siegeCost = view.UnitCost(UnitType.Trebuchet);
            if (budget < siegeCost)
            {
                _saving = true;   // the Barracks wait too, so the ore can pile up
                return;
            }

            commands.Add(new CommandRequest { Type = CommandType.Produce, BuildingId = workshop.Id, UnitType = UnitType.Trebuchet, X = rally.X, Y = rally.Y });
            budget -= siegeCost;
            siege++;
            total++;
        }

        foreach (var barracks in view.MyBuildings.Where(b => b.Type == BuildingType.Barracks && b.Completed && b.Production.Count < 2))
        {
            var wantArchers = (int)Math.Round(s.ArcherPercent * (total + 1) / 100.0);
            var type = archers < wantArchers ? UnitType.Archer : UnitType.Soldier;
            var cost = view.UnitCost(type);
            if (budget < cost)
            {
                return;
            }

            commands.Add(new CommandRequest { Type = CommandType.Produce, BuildingId = barracks.Id, UnitType = type, X = rally.X, Y = rally.Y });
            budget -= cost;
            total++;
            if (type == UnitType.Archer)
            {
                archers++;
            }
        }
    }

    // Defensive posture: we fight where the towers can help and don't chase. When several enemy armies are
    // around and none has reached our buildings yet, we wait and let them fight each other.
    private static UnitDto? Engageable(BotView view, UnitDto? threat)
    {
        if (threat is null)
        {
            return null;
        }

        var cc = view.CommandCenter!;
        var anchors = view.MyBuildings
            .Where(b => b.Completed && b.Type is BuildingType.GuardTower or BuildingType.CommandCenter && Dist(b.X, b.Y, cc.X, cc.Y) <= HomeRadius)
            .ToList();
        var nearBuilding = view.MyBuildings
            .Where(b => Dist(b.X, b.Y, cc.X, cc.Y) <= HomeRadius)
            .Select(b => Dist(b.X, b.Y, threat.X, threat.Y))
            .DefaultIfEmpty(99)
            .Min();

        // Too far from the towers' cover and not touching a building yet: let it come to us.
        if (anchors.Count > 0 && anchors.Min(a => Dist(a.X, a.Y, threat.X, threat.Y)) > 8 && nearBuilding > 3)
        {
            return null;
        }

        // Two or more enemy owners close by and nobody on our doorstep: stay put while they hit each other.
        var owners = view.EnemyUnits.Where(e => Dist(e.X, e.Y, cc.X, cc.Y) <= 20).Select(e => e.Owner).Distinct().Count();
        if (owners >= 2 && nearBuilding > 4)
        {
            return null;
        }

        return threat;
    }

    private int _lastSiegeMoveTick = -100;

    // Home trebuchets: parked in a loose line just in front of the Command Center (they reach past the towers).
    // When enemies come, they stand about 5-6 tiles back from the fight, on our side, so the soldiers screen
    // them and the shells land on the enemies fighting our front line; if the fight reaches them they back up.
    private void PositionHomeSiege(BotView view, List<CommandRequest> commands, List<UnitDto> trebuchets, UnitDto? threat)
    {
        if (trebuchets.Count == 0)
        {
            return;
        }

        var tick = view.State.Tick;
        if (tick - _lastSiegeMoveTick < (threat is null ? 20 : 5))
        {
            return;
        }

        _lastSiegeMoveTick = tick;
        var cc = view.CommandCenter!;
        var enemy = view.EnemyStart(cc.X, cc.Y);
        double dx = enemy is null ? 1 : enemy.X - cc.X, dy = enemy is null ? 1 : enemy.Y - cc.Y;
        var dl = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        dx /= dl;
        dy /= dl;

        double px, py;
        if (threat is null)
        {
            px = cc.X + dx * 2;
            py = cc.Y + dy * 2;
        }
        else
        {
            var cluster = view.EnemyUnits.Where(e => Dist(e.X, e.Y, threat.X, threat.Y) <= 6).ToList();
            double cx = cluster.Average(e => e.X), cy = cluster.Average(e => e.Y);
            double vx = cc.X - cx, vy = cc.Y - cy;
            var vl = Math.Sqrt(vx * vx + vy * vy);
            if (vl < 4)
            {
                // the fight is on top of the Command Center: fall back behind it
                px = cc.X - dx * 3;
                py = cc.Y - dy * 3;
            }
            else
            {
                px = cx + vx / vl * 5.5;
                py = cy + vy / vl * 5.5;
            }
        }

        // a loose line across the front, a row further back every five
        double lx = -dy, ly = dx;
        for (var i = 0; i < trebuchets.Count; i++)
        {
            var across = (i % 5 - 2) * 1.6;
            var back = i / 5 * 1.6;
            var spot = OpenNear(view, (int)Math.Round(px + lx * across - dx * back), (int)Math.Round(py + ly * across - dy * back));
            var t = trebuchets[i];
            if (Dist(t.X, t.Y, spot.X, spot.Y) <= 1 && t.Activity == UnitActivity.Idle)
            {
                continue;   // already in place: leave it idle so it fires
            }

            if (t.Destination is { } d && Dist(d.X, d.Y, spot.X, spot.Y) <= 1)
            {
                continue;
            }

            commands.Add(new CommandRequest { Type = CommandType.Move, UnitIds = [t.Id], X = spot.X, Y = spot.Y });
        }
    }

    // Where the home army waits: just behind the first finished home tower, else the default rally point.
    private static (int X, int Y) HomeRally(BotView view)
    {
        var cc = view.CommandCenter!;
        var tower = view.MyBuildings
            .Where(b => b.Type == BuildingType.GuardTower && b.Completed && Dist(b.X, b.Y, cc.X, cc.Y) <= HomeRadius)
            .OrderBy(b => b.Id)
            .FirstOrDefault();
        if (tower is null)
        {
            return view.RallyPoint();
        }

        return OpenNear(view, tower.X + Math.Sign(cc.X - tower.X) * 2, tower.Y + Math.Sign(cc.Y - tower.Y) * 2);
    }

    // Where the field army waits once the far depot is finished: a few tiles in front of it. Null until then.
    private static (int X, int Y)? FarRally(BotView view)
    {
        var cc = view.CommandCenter!;
        // The field we are working now: the newest finished far depot that still has ore beside it.
        var far = view.MyBuildings
            .Where(b => b.Type == BuildingType.ResourceDepot && b.Completed && Dist(b.X, b.Y, cc.X, cc.Y) > HomeRadius)
            .OrderByDescending(b => b.Id)
            .ToList();
        var depot = far.FirstOrDefault(d => view.State.Resources.Any(r => Dist(r.X, r.Y, d.X, d.Y) <= 9)) ?? far.FirstOrDefault();
        if (depot is null)
        {
            return null;
        }

        var enemy = view.EnemyStart(cc.X, cc.Y);
        var dx = enemy is null ? 0 : Math.Sign(enemy.X - depot.X);
        var dy = enemy is null ? 0 : Math.Sign(enemy.Y - depot.Y);
        return OpenNear(view, depot.X + dx * 3, depot.Y + dy * 3);
    }

    private static UnitDto? NearestThreat(BotView view, IReadOnlyList<BuildingDto> buildings, int fromX, int fromY) =>
        view.EnemyUnits
            .Where(e => buildings.Any(b => Dist(b.X, b.Y, e.X, e.Y) <= DefenseRadius))
            .OrderBy(e => Dist(fromX, fromY, e.X, e.Y))
            .FirstOrDefault();

    // A group defends against a threat if there is one, else gathers at its rally point.
    private static void Guard(List<CommandRequest> commands, List<UnitDto> group, (int X, int Y) rally, UnitDto? threat)
    {
        if (group.Count == 0)
        {
            return;
        }

        if (threat is not null)
        {
            var defenders = group
                .Where(u => u.Activity is UnitActivity.Idle or UnitActivity.Moving)
                .Select(u => u.Id)
                .ToArray();
            if (defenders.Length > 0)
            {
                commands.Add(new CommandRequest { Type = CommandType.Attack, UnitIds = defenders, X = threat.X, Y = threat.Y });
            }

            return;
        }

        var stragglers = group
            .Where(u => u.Activity == UnitActivity.Idle && Dist(u.X, u.Y, rally.X, rally.Y) > 3)
            .Select(u => u.Id)
            .ToArray();
        if (stragglers.Length > 0)
        {
            commands.Add(new CommandRequest { Type = CommandType.Move, UnitIds = stragglers, X = rally.X, Y = rally.Y });
        }
    }

    // Worker raid: fight any defenders that come close, otherwise hunt visible workers. With nothing to kill in
    // sight, walk a ring around the enemy start (Move orders never attack, and the ring tiles have no building
    // within 2 tiles), so buildings - above all the Command Center - are not touched.
    private void RunRaid(BotView view, List<UnitDto> raiders, List<CommandRequest> commands)
    {
        var tick = view.State.Tick;
        var player = view.State.Players.FirstOrDefault(p => p.Slot == _raidSlot && !p.Eliminated);
        if (player is null || raiders.Count == 0)
        {
            Console.WriteLine($"[tick {tick}] raid over (target gone): raiders rejoin the army");
            _raid.Clear();
            return;
        }

        if (tick % 2 != 0)
        {
            return;
        }

        var ids = raiders.Select(r => r.Id).ToArray();
        double mx = raiders.Average(r => r.X), my = raiders.Average(r => r.Y);
        var theirs = view.EnemyUnits.Where(e => e.Owner == _raidSlot).ToList();

        var defender = theirs
            .Where(e => e.Type is UnitType.Soldier or UnitType.Archer or UnitType.Trebuchet or UnitType.Scout
                        && raiders.Any(r => Dist(r.X, r.Y, e.X, e.Y) <= 8))
            .OrderBy(e => Math.Abs(e.X - mx) + Math.Abs(e.Y - my))
            .FirstOrDefault();
        if (defender is not null)
        {
            commands.Add(new CommandRequest { Type = CommandType.Attack, UnitIds = ids, X = defender.X, Y = defender.Y });
            return;
        }

        var worker = theirs
            .Where(e => e.Type == UnitType.Worker)
            .OrderBy(e => Math.Abs(e.X - mx) + Math.Abs(e.Y - my))
            .FirstOrDefault();
        if (worker is not null)
        {
            _raidLastWorkerTick = tick;
            commands.Add(new CommandRequest { Type = CommandType.Attack, UnitIds = ids, X = worker.X, Y = worker.Y });
            return;
        }

        // No workers for a while: pull back out of the towers' reach and only peek in now and then.
        var quiet = tick - _raidLastWorkerTick;
        if (quiet > 60 && (quiet - 60) % 150 >= 30)
        {
            var cc = view.CommandCenter!;
            double bx = cc.X - player.StartPosition.X, by = cc.Y - player.StartPosition.Y;
            var bl = Math.Max(1, Math.Sqrt(bx * bx + by * by));
            var stage = OpenNear(view, (int)Math.Round(player.StartPosition.X + bx / bl * 13), (int)Math.Round(player.StartPosition.Y + by / bl * 13));
            var back = raiders
                .Where(r => !(r.Destination is { } d0 && d0.X == stage.X && d0.Y == stage.Y))
                .Where(r => !(r.Activity == UnitActivity.Idle && Dist(r.X, r.Y, stage.X, stage.Y) <= 3))
                .Select(r => r.Id)
                .ToArray();
            if (back.Length > 0)
            {
                commands.Add(new CommandRequest { Type = CommandType.Move, UnitIds = back, X = stage.X, Y = stage.Y });
            }

            return;
        }

        // nothing to kill in sight: circle the enemy start at 4 tiles, a new spot every 25 ticks
        var ring = new List<(int X, int Y)>();
        for (var a = 0; a < 16; a++)
        {
            var ang = a * Math.PI / 8;
            var x = (int)Math.Round(player.StartPosition.X + Math.Cos(ang) * 4);
            var y = (int)Math.Round(player.StartPosition.Y + Math.Sin(ang) * 4);
            if (view.IsOpen(x, y) && !view.EnemyBuildings.Any(b => Dist(b.X, b.Y, x, y) <= 2))
            {
                ring.Add((x, y));
            }
        }

        if (ring.Count == 0)
        {
            return;
        }

        // start with the ring tile nearest the raiders so they arrive from our side, then go around
        var first = ring.Select((p, i) => (p, i)).OrderBy(t => Math.Abs(t.p.X - mx) + Math.Abs(t.p.Y - my)).First().i;
        var spot = ring[(first + (tick - _raidStartTick(tick)) / 25) % ring.Count];
        var movers = raiders
            .Where(r => !(r.Destination is { } d && d.X == spot.X && d.Y == spot.Y))
            .Where(r => !(r.Activity == UnitActivity.Idle && r.X == spot.X && r.Y == spot.Y))
            .Select(r => r.Id)
            .ToArray();
        if (movers.Length > 0)
        {
            commands.Add(new CommandRequest { Type = CommandType.Move, UnitIds = movers, X = spot.X, Y = spot.Y });
        }
    }

    private int _raidStart = -1;
    private int _raidStartTick(int tick)
    {
        if (_raidStart < 0 || _raid.Count == 0) { _raidStart = tick; }
        return _raidStart;
    }

    // Left / center / right are seen from our base looking toward the middle of the map.
    private static Dictionary<string, PlayerSummaryDto> ResolveSides(BotView view)
    {
        var result = new Dictionary<string, PlayerSummaryDto>();
        var cc = view.CommandCenter;
        var me = view.State.You.Slot;
        var enemies = view.State.Players.Where(p => p.Slot != me && !p.Eliminated).ToList();
        if (cc is null || enemies.Count == 0)
        {
            return result;
        }

        double ax = view.Map.Width / 2.0 - cc.X, ay = view.Map.Height / 2.0 - cc.Y;
        var len = Math.Sqrt(ax * ax + ay * ay);
        if (len < 1) { ax = 1; ay = 0; len = 1; }
        ax /= len;
        ay /= len;

        // Positive = to our left (as seen on screen, y grows downward).
        double Lateral(PlayerSummaryDto p)
        {
            double vx = p.StartPosition.X - cc.X, vy = p.StartPosition.Y - cc.Y;
            var vl = Math.Max(1, Math.Sqrt(vx * vx + vy * vy));
            return (vx * ay - vy * ax) / vl;
        }

        result["left"] = enemies.OrderByDescending(Lateral).First();
        result["right"] = enemies.OrderBy(Lateral).First();
        result["center"] = enemies.OrderBy(p => Math.Abs(Lateral(p))).First();
        return result;
    }

    private static string DescribeSides(BotView view)
    {
        var sides = ResolveSides(view);
        if (sides.Count == 0) { return ""; }
        return string.Join(" | ", new[] { "left", "center", "right" }.Select(k => $"{k}: {sides[k].Name}"));
    }

    // The attack goal: the chosen enemy's nearest visible building (else its start), or the nearest enemy.
    private (int X, int Y)? PickTarget(BotView view)
    {
        if (_targetSlot is { } slot)
        {
            var player = view.State.Players.FirstOrDefault(p => p.Slot == slot && !p.Eliminated);
            if (player is not null)
            {
                var cc = view.CommandCenter!;
                var building = view.EnemyBuildings
                    .Where(b => b.Owner == slot)
                    .OrderBy(b => Dist(b.X, b.Y, cc.X, cc.Y))
                    .FirstOrDefault();
                return building is null ? (player.StartPosition.X, player.StartPosition.Y) : (building.X, building.Y);
            }

            _targetSlot = null;   // that enemy is out: back to the nearest one
        }

        return view.AttackTarget();
    }

    private void CommandArmy(BotView view, BotSettings s, List<CommandRequest> commands)
    {
        var tick = view.State.Tick;
        var fighters = view.Army.Where(u => u.Type is UnitType.Soldier or UnitType.Archer).ToList();   // the raider scout is separate
        var siege = view.Siege.ToList();
        var cc = view.CommandCenter!;

        // 1. Forget the dead. Retire the wave once its fighters are mostly gone; survivors become home units.
        _wave.IntersectWith(fighters.Select(u => u.Id));
        _siegeWave.IntersectWith(siege.Select(u => u.Id));
        // Trebuchets keep marching even after their escorts die, so the wave only ends when they are gone too.
        if (_waveSize > 0 && _siegeWave.Count == 0 && _wave.Count < Math.Max(2, _waveSize / 3))
        {
            _wave.Clear();
            _waveSize = 0;
        }

        // 2. The buttons. Recall brings everyone home; Attack sends everyone except the garrison.
        if (control.TakeRecall())
        {
            Console.WriteLine($"[tick {tick}] RECALL: wave of {_wave.Count + _siegeWave.Count} coming home");
            _wave.Clear();
            _siegeWave.Clear();
            _raid.Clear();
            _waveSize = 0;
        }

        _raid.IntersectWith(fighters.Select(u => u.Id));
        var pressed = control.TakeAttack();
        var raidPress = pressed && control.TakeRaid();
        if (pressed && !raidPress && _raid.Count > 0)
        {
            Console.WriteLine($"[tick {tick}] raid ended: {_raid.Count} raiders rejoin the army");
            _raid.Clear();
        }

        var home = fighters.Where(u => !_wave.Contains(u.Id) && !_raid.Contains(u.Id)).OrderBy(u => u.Id).ToList();
        if (pressed)
        {
            var dir = control.TakeDirection();
            var sides = ResolveSides(view);
            _targetSlot = dir is not null && sides.TryGetValue(dir, out var chosen) ? chosen.Slot : null;
            _lastRetargetTick = -RetargetEveryTicks;   // redirect a wave that is already out, right away
            Console.WriteLine($"[tick {tick}] attack target: {(_targetSlot is { } t ? $"{dir} ({sides[dir!].Name})" : "nearest enemy")}");
        }
        if (raidPress)
        {
            var slot = _targetSlot
                       ?? view.State.Players.Where(p => p.Slot != view.State.You.Slot && !p.Eliminated)
                           .OrderBy(p => Dist(p.StartPosition.X, p.StartPosition.Y, cc.X, cc.Y)).Select(p => (int?)p.Slot).FirstOrDefault();
            var keep = (int)Math.Round(home.Count * s.DefendPercent / 100.0);
            var raiders = home.Skip(keep).Where(u => u.Type == UnitType.Soldier).ToList();
            if (slot is null || raiders.Count == 0)
            {
                Console.WriteLine($"[tick {tick}] raid ordered, but there are no soldiers to spare or no enemy left");
            }
            else
            {
                _raidSlot = slot.Value;
                _raidLastWorkerTick = tick;
                foreach (var u in raiders) { _raid.Add(u.Id); }
                home = home.Where(u => !_raid.Contains(u.Id)).ToList();
                Console.WriteLine($"[tick {tick}] RAID: {_raid.Count} soldiers go after the workers of slot {_raidSlot}; trebuchets stay home");
            }

            pressed = false;
        }

        var auto = s.AttackAtTick > 0 && tick >= s.AttackAtTick && _waveSize == 0 && home.Count >= s.AttackThreshold;
        if (pressed || auto)
        {
            var stay = (int)Math.Round(home.Count * s.DefendPercent / 100.0);
            var goers = home.Skip(stay).ToList();
            if (goers.Count == 0 && siege.Count == 0)
            {
                Console.WriteLine($"[tick {tick}] attack ordered, but there are no troops to spare (garrison keeps {stay})");
            }
            else
            {
                foreach (var u in goers)
                {
                    _wave.Add(u.Id);
                }

                foreach (var t in siege)
                {
                    _siegeWave.Add(t.Id);
                }

                _waveSize = Math.Max(1, _wave.Count);
                _lastRetargetTick = -RetargetEveryTicks;
                home = home.Take(stay).ToList();
                Console.WriteLine($"[tick {tick}] ATTACK: {_wave.Count} troops and {_siegeWave.Count} trebuchets go out, garrison {stay}");
            }
        }

        // 2b. The raid: kill the target's workers and stay off its buildings, so its Command Center is left standing.
        if (_raid.Count > 0)
        {
            RunRaid(view, fighters.Where(u => _raid.Contains(u.Id)).ToList(), commands);
        }

        // 3. The wave marches. With trebuchets it rolls as a formation: they attack-move, the escorts ring them.
        if ((_wave.Count > 0 || _siegeWave.Count > 0) && PickTarget(view) is { } target)
        {
            var retarget = tick - _lastRetargetTick >= RetargetEveryTicks;
            var escorts = fighters.Where(u => _wave.Contains(u.Id)).ToList();
            var rolling = siege.Where(t => _siegeWave.Contains(t.Id)).ToList();

            if (rolling.Count == 0)
            {
                var ids = escorts
                    .Where(u => retarget || u.Activity == UnitActivity.Idle)
                    .Select(u => u.Id)
                    .ToArray();
                if (ids.Length > 0)
                {
                    commands.Add(new CommandRequest { Type = CommandType.Attack, UnitIds = ids, X = target.X, Y = target.Y });
                }
            }
            else
            {
                if (retarget || rolling.Any(t => t.Activity == UnitActivity.Idle))
                {
                    commands.Add(new CommandRequest
                    {
                        Type = CommandType.Attack,
                        UnitIds = rolling.Select(t => t.Id).ToArray(),
                        X = target.X,
                        Y = target.Y,
                    });
                }

                if (tick % EscortEveryTicks == 0)
                {
                    // The ring is centred a step ahead of the trebuchets, toward the target.
                    var cx = (int)Math.Round(rolling.Average(t => t.X));
                    var cy = (int)Math.Round(rolling.Average(t => t.Y));
                    cx += Math.Sign(target.X - cx);
                    cy += Math.Sign(target.Y - cy);
                    for (var i = 0; i < escorts.Count; i++)
                    {
                        var escort = escorts[i];
                        var (dx, dy) = EscortRing[i % EscortRing.Length];
                        var x = Math.Clamp(cx + dx, 0, view.Map.Width - 1);
                        var y = Math.Clamp(cy + dy, 0, view.Map.Height - 1);

                        // An escort already fighting near the siege keeps at it; one that wandered off or is idle falls in.
                        var nearSiege = Dist(escort.X, escort.Y, cx, cy) <= 4;
                        if ((escort.Activity == UnitActivity.Attacking && nearSiege)
                            || (escort.Destination is { } d && d.X == x && d.Y == y && escort.Activity != UnitActivity.Idle)
                            || (escort.Activity == UnitActivity.Idle && escort.X == x && escort.Y == y))
                        {
                            continue;
                        }

                        commands.Add(new CommandRequest { Type = CommandType.Attack, UnitIds = [escort.Id], X = x, Y = y });
                    }
                }
            }

            if (retarget)
            {
                _lastRetargetTick = tick;
            }
        }

        // 4. The escort that left with the builder stays at the new site until the depot stands (or 400 ticks pass).
        var farRally = FarRally(view);
        var escortUnits = new List<UnitDto>();
        if (_escortIds.Count > 0)
        {
            var arrived = _escortSite is { } es
                          && view.MyBuildings.Any(b => b.Type == BuildingType.ResourceDepot && b.Completed && Dist(b.X, b.Y, es.X, es.Y) <= 4);
            if (arrived || tick - _escortSinceTick > 400)
            {
                _escortIds.Clear();   // their depot stands: they join the field group below
                _escortSite = null;
            }
            else
            {
                escortUnits = home.Where(u => _escortIds.Contains(u.Id)).ToList();
                home = home.Where(u => !_escortIds.Contains(u.Id)).ToList();
            }
        }

        if (escortUnits.Count > 0 && FindEscortSite(view) is { } escortSite)
        {
            var strays = escortUnits
                .Where(u => u.Activity == UnitActivity.Idle && Dist(u.X, u.Y, escortSite.X, escortSite.Y) > 3)
                .Select(u => u.Id)
                .ToArray();
            if (strays.Length > 0)
            {
                commands.Add(new CommandRequest { Type = CommandType.Attack, UnitIds = strays, X = escortSite.X, Y = escortSite.Y });
            }
        }

        // 5. Home units. Once the far depot stands, only the garrison stays home; the rest guard the far field.
        //    Trebuchets always stay behind the towers until the Attack button sends them.
        var homeRally = HomeRally(view);
        var homeBuildings = view.MyBuildings.Where(b => Dist(b.X, b.Y, cc.X, cc.Y) <= HomeRadius).ToList();
        var homeThreat = Engageable(view, NearestThreat(view, homeBuildings, cc.X, cc.Y));
        PositionHomeSiege(view, commands, siege.Where(t => !_siegeWave.Contains(t.Id)).ToList(),
            NearestThreat(view, homeBuildings, cc.X, cc.Y));

        if (farRally is { } far)
        {
            var stay = (int)Math.Round(home.Count * s.DefendPercent / 100.0);
            var garrison = home.Take(stay).ToList();
            var field = home.Skip(stay).ToList();
            var fieldThreat = NearestThreat(view, view.MyBuildings, far.X, far.Y);

            Guard(commands, garrison, homeRally, homeThreat);
            Guard(commands, field, far, fieldThreat);
        }
        else
        {
            Guard(commands, home, homeRally, homeThreat ?? Engageable(view, NearestThreat(view, view.MyBuildings, cc.X, cc.Y)));
        }
    }

    // Where the escort is headed: the depot site the expedition was sent to.
    private (int X, int Y)? FindEscortSite(BotView view) => _escortSite;

    // ---- Answering enemy trebuchets ----

    // Enemy shells are visible while they fly: each names the tile it will land on and the tick it lands.
    // Anything of ours standing on that tile steps aside; buildings can't, but units and workers can.
    private void DodgeShells(BotView view, List<CommandRequest> commands)
    {
        var me = view.State.You.Slot;
        var tick = view.State.Tick;
        var incoming = view.State.Shells.Where(sh => sh.Owner != me && sh.LandsTick >= tick).ToList();
        if (incoming.Count == 0)
        {
            return;
        }

        var tiles = incoming.Select(sh => (sh.X, sh.Y)).ToHashSet();
        var moved = 0;
        foreach (var unit in view.MyUnits.Where(u => tiles.Contains((u.X, u.Y))))
        {
            (int X, int Y)? step = null;
            for (var r = 2; r <= 3 && step is null; r++)
            {
                for (var dy = -r; dy <= r && step is null; dy++)
                {
                    for (var dx = -r; dx <= r && step is null; dx++)
                    {
                        var x = unit.X + dx;
                        var y = unit.Y + dy;
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && view.IsOpen(x, y) && !tiles.Contains((x, y)))
                        {
                            step = (x, y);
                        }
                    }
                }
            }

            if (step is { } spot)
            {
                commands.Add(new CommandRequest { Type = CommandType.Move, UnitIds = [unit.Id], X = spot.X, Y = spot.Y });
                moved++;
            }
        }

        if (moved > 0 && tick - _lastDodgeNote >= 10)
        {
            _lastDodgeNote = tick;
            Console.WriteLine($"[tick {tick}] enemy shells incoming: {moved} of our units step off the target tiles");
        }
    }

    // A visible enemy trebuchet near our buildings is hunted down: it can't hit anything inside 3 tiles and
    // it is slow, so our troops run at it. If we only see the shells, we attack-move to where they were fired from.
    private void HuntEnemySiege(BotView view, List<CommandRequest> commands)
    {
        var me = view.State.You.Slot;
        var tick = view.State.Tick;

        var targets = view.EnemyUnits
            .Where(e => e.Type == UnitType.Trebuchet)
            .Where(e => view.MyBuildings.Any(b => Dist(b.X, b.Y, e.X, e.Y) <= 16))
            .Select(e => (Id: (int?)e.Id, e.X, e.Y))
            .ToList();
        if (targets.Count == 0)
        {
            // Not visible: find where the shells came from.
            targets = view.State.Shells
                .Where(sh => sh.Owner != me && sh.LandsTick >= tick)
                .Select(sh => (Id: (int?)null, X: sh.FromX, Y: sh.FromY))
                .Distinct()
                .ToList();
        }

        if (targets.Count == 0)
        {
            return;
        }

        var target = targets.OrderBy(t => view.MyBuildings.Min(b => Dist(b.X, b.Y, t.X, t.Y))).First();

        // Enough to beat what guards it: the nearest eight, or the whole home army if it is ringed by soldiers.
        var guards = view.EnemyUnits.Count(e => e.Type is UnitType.Soldier or UnitType.Archer && Dist(e.X, e.Y, target.X, target.Y) <= 5);
        var available = view.Army
            .Where(u => u.Type is UnitType.Soldier or UnitType.Archer && !_wave.Contains(u.Id))
            .OrderBy(u => Dist(u.X, u.Y, target.X, target.Y))
            .ToList();
        var hunters = available.Take(guards >= 4 ? available.Count : 8).ToList();
        if (hunters.Count == 0)
        {
            return;
        }

        if (tick - _lastHuntNote >= 20)
        {
            _lastHuntNote = tick;
            Console.WriteLine($"[tick {tick}] enemy trebuchet at ({target.X},{target.Y}), {guards} guards: sending {hunters.Count} to kill it");
        }

        var ids = hunters.Select(u => u.Id).ToArray();
        commands.Add(target.Id is { } id
            ? new CommandRequest { Type = CommandType.Attack, UnitIds = ids, TargetId = id }
            : new CommandRequest { Type = CommandType.Attack, UnitIds = ids, X = target.X, Y = target.Y });
    }

    // ---- Rogue scout ----

    // One scout, built once the upgrades are done. Rebuilt if it dies, at most three times in all.
    private void MaybeBuildRaider(BotView view, BotSettings s, List<CommandRequest> commands, ref int budget)
    {
        if (s.RaiderScout == 0 || _raidersBuilt >= 3 || view.State.Tick > 1650)
        {
            return;
        }

        if (view.Army.Any(u => u.Type == UnitType.Scout)
            || view.MyBuildings.Any(b => b.Production.Any(p => p.UnitType == UnitType.Scout))
            || view.State.Tick - _raiderQueuedTick < 150)
        {
            return;
        }

        var you = view.State.You;
        if (UpgradeOrder.Take(s.UpgradeCount).Any(u => !you.Upgrades.Contains(u)))
        {
            return;
        }

        var cost = view.UnitCost(UnitType.Scout);
        var barracks = view.MyBuildings.FirstOrDefault(b => b.Type == BuildingType.Barracks && b.Completed && b.Production.Count < 2);
        if (budget < cost || barracks is null)
        {
            return;
        }

        Console.WriteLine($"[tick {view.State.Tick}] training raider scout #{_raidersBuilt + 1}");
        commands.Add(new CommandRequest { Type = CommandType.Produce, BuildingId = barracks.Id, UnitType = UnitType.Scout });
        _raiderQueuedTick = view.State.Tick;
        _raidersBuilt++;
        budget -= cost;
    }

    private void RunRaider(BotView view, BotSettings s, List<CommandRequest> commands)
    {
        var scout = view.Army.Where(u => u.Type == UnitType.Scout).OrderBy(u => u.Id).FirstOrDefault();
        if (s.RaiderScout == 0 || scout is null)
        {
            _raiderId = -1;
            return;
        }

        var tick = view.State.Tick;
        var cc = view.CommandCenter!;
        if (scout.Id != _raiderId)
        {
            _raiderId = scout.Id;
            _raiderBase = 0;
            _raiderPoint = 0;
            _raiderTargetId = -1;
            Console.WriteLine($"[tick {tick}] raider scout {scout.Id} is on the loose");
        }

        // 1. Danger (an enemy tower close by, or soldiers/archers): run directly away from it.
        var threats = new List<(int X, int Y)>();
        threats.AddRange(view.EnemyBuildings
            .Where(b => b.Type == BuildingType.GuardTower && b.Completed && Dist(b.X, b.Y, scout.X, scout.Y) <= 7)
            .Select(b => (b.X, b.Y)));
        threats.AddRange(view.EnemyUnits
            .Where(e => (e.Type == UnitType.Soldier || e.Type == UnitType.Archer) && Dist(e.X, e.Y, scout.X, scout.Y) <= 6)
            .Select(e => (e.X, e.Y)));

        if (threats.Count > 0)
        {
            var t = threats.OrderBy(p => Dist(p.X, p.Y, scout.X, scout.Y)).First();
            var dx = Math.Sign(scout.X - t.X);
            var dy = Math.Sign(scout.Y - t.Y);
            if (dx == 0 && dy == 0)
            {
                dx = Math.Sign(cc.X - scout.X);
                dy = Math.Sign(cc.Y - scout.Y);
            }

            (int X, int Y)? flee = null;
            for (var d = 10; d >= 4 && flee is null; d -= 2)
            {
                var x = Math.Clamp(scout.X + dx * d, 1, view.Map.Width - 2);
                var y = Math.Clamp(scout.Y + dy * d, 1, view.Map.Height - 2);
                if (view.IsOpen(x, y))
                {
                    flee = (x, y);
                }
            }

            var spot = flee ?? OpenNear(view, cc.X, cc.Y);
            if (tick > _raiderFleeUntil)
            {
                _raiderPoint = (_raiderPoint + 1) % Ring.Length;   // new run: try a different side next time
                Console.WriteLine($"[tick {tick}] raider scout runs from danger at ({t.X},{t.Y})");
            }

            _raiderFleeUntil = tick + 4;
            _raiderTargetId = -1;
            commands.Add(new CommandRequest { Type = CommandType.Move, UnitIds = [scout.Id], X = spot.X, Y = spot.Y });
            return;
        }

        if (tick <= _raiderFleeUntil)
        {
            return;   // still running; don't turn back yet
        }

        // 2. Hunt: the nearest enemy worker that isn't under a tower or in a crowd.
        var prey = view.EnemyUnits
            .Where(w => w.Type == UnitType.Worker)
            .Where(w => !view.EnemyBuildings.Any(b => b.Type == BuildingType.GuardTower && b.Completed && Dist(b.X, b.Y, w.X, w.Y) <= 7))
            .Where(w => view.EnemyUnits.Count(o => o.Type == UnitType.Worker && Dist(o.X, o.Y, w.X, w.Y) <= 2) <= 3)
            .OrderBy(w => Dist(scout.X, scout.Y, w.X, w.Y))
            .ThenBy(w => w.Hp)
            .FirstOrDefault();

        if (prey is not null)
        {
            if (prey.Id != _raiderTargetId)
            {
                _raiderTargetId = prey.Id;
                Console.WriteLine($"[tick {tick}] raider scout hunting worker {prey.Id} at ({prey.X},{prey.Y})");
            }

            if (scout.TargetId != prey.Id || scout.Activity == UnitActivity.Idle)
            {
                commands.Add(new CommandRequest { Type = CommandType.Attack, UnitIds = [scout.Id], TargetId = prey.Id });
            }

            return;
        }

        // 3. Nothing to hunt: walk a ring around each enemy base in turn.
        var bases = view.State.Players
            .Where(p => p.Slot != view.State.You.Slot && !p.Eliminated)
            .OrderBy(p => Dist(p.StartPosition.X, p.StartPosition.Y, cc.X, cc.Y))
            .ToList();
        if (bases.Count == 0)
        {
            return;
        }

        var wp = RaiderWaypoint(view, bases);
        if (Dist(scout.X, scout.Y, wp.X, wp.Y) <= 2)
        {
            _raiderPoint++;
            if (_raiderPoint >= Ring.Length)
            {
                _raiderPoint = 0;
                _raiderBase++;
            }

            wp = RaiderWaypoint(view, bases);
        }

        if (scout.Activity == UnitActivity.Idle || scout.Destination is not { } dest || dest.X != wp.X || dest.Y != wp.Y)
        {
            commands.Add(new CommandRequest { Type = CommandType.Move, UnitIds = [scout.Id], X = wp.X, Y = wp.Y });
        }
    }

    private (int X, int Y) RaiderWaypoint(BotView view, List<PlayerSummaryDto> bases)
    {
        var b = bases[_raiderBase % bases.Count].StartPosition;
        var (ox, oy) = Ring[_raiderPoint % Ring.Length];
        return OpenNear(view, b.X + ox, b.Y + oy);
    }

    // ---- Helpers ----

    // The nearest open tile to (x, y), searching outward up to 4 tiles.
    private static (int X, int Y) OpenNear(BotView view, int x, int y)
    {
        x = Math.Clamp(x, 1, view.Map.Width - 2);
        y = Math.Clamp(y, 1, view.Map.Height - 2);
        for (var r = 0; r <= 4; r++)
        {
            for (var dy = -r; dy <= r; dy++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && view.IsOpen(x + dx, y + dy))
                    {
                        return (x + dx, y + dy);
                    }
                }
            }
        }

        return (x, y);
    }

    private static int Dist(int x1, int y1, int x2, int y2) => Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));
}