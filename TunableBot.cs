using NetRts.Bots;
using NetRts.Protocol;

public class TunableBot(SettingsStore store) : IBotStrategy
{
    private const int DefenseRadius = 10;
    private const int RetargetEveryTicks = 15;
    private const int HomeRadius = 12;     // buildings this close to the Command Center are "home"
    private const int TierOneCount = 3;    // Harvesting1, Weapons1, Armor1

    // Research order. The UpgradeCount slider = how many of these we buy.
    private static readonly UpgradeType[] UpgradeOrder =
    [
        UpgradeType.Harvesting1, UpgradeType.Weapons1, UpgradeType.Armor1,
        UpgradeType.Weapons2, UpgradeType.Armor2,
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
    private HashSet<int> _wave = [];
    private int _waveSize;
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

        LogEvents(view);
        if (tick % 100 == 0)
        {
            var cc = view.CommandCenter;
            var homeOre = view.State.Resources.Where(d => Dist(d.X, d.Y, cc.X, cc.Y) <= 9).Sum(d => d.Remaining);
            Console.WriteLine($"[tick {tick}] workers {view.Workers.Count}, army {view.Army.Count}, ore {view.State.You.Resources}, " +
                              $"income/min {view.State.You.IncomePerMinute}, upgrades {view.State.You.Upgrades.Count}, home ore left {homeOre}");
        }

        RememberDeposits(view);

        // Priority order. Each step only spends if nothing above it is waiting for ore.
        TrainWorkers(view, s, commands, ref budget);
        FollowBuildOrder(view, s, commands, ref budget);   // lost buildings and the TechLab save first
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
        RunRaider(view, s, commands);
        RetreatWorkers(view, commands);
        PutIdleWorkersToWork(view, commands);
        ScoutTour(view, s, commands);   // last on purpose: a later command overrides an earlier one

        return commands;
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
        var usable = safe.Where(kv => !Threatened(view, kv.Value.X, kv.Value.Y)).ToList();
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
        if (s.ExpandDepot == 0 || tick < 150 || tick - _expansionOrderTick < 60)
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

        Console.WriteLine($"[tick {tick}] building a depot at far ore near ({cx},{cy}), site {site}, builder {builder.Id}");
        commands.Add(new CommandRequest
        {
            Type = CommandType.Build,
            UnitIds = [builder.Id],
            BuildingType = BuildingType.ResourceDepot,
            X = site.Value.X,
            Y = site.Value.Y,
        });
        _expansionOrderTick = tick;
        budget -= cost;
    }

    // Once the far depot is finished, one Guard Tower goes up beside it, on the enemy side.
    private void BuildFarTower(BotView view, BotSettings s, List<CommandRequest> commands, ref int budget)
    {
        var tick = view.State.Tick;
        if (s.FarTower == 0 || tick - _farTowerOrderTick < 60)
        {
            return;
        }

        var cc = view.CommandCenter!;
        var depot = view.MyBuildings
            .Where(b => b.Type == BuildingType.ResourceDepot && b.Completed && Dist(b.X, b.Y, cc.X, cc.Y) > HomeRadius)
            .OrderBy(b => b.Id)
            .FirstOrDefault();
        if (depot is null)
        {
            return;
        }

        if (view.MyBuildings.Any(b => b.Type == BuildingType.GuardTower && Dist(b.X, b.Y, depot.X, depot.Y) <= 8))
        {
            return;   // already guarded (finished or under construction)
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

        var site = view.FindBuildSite(SitePreference.TowardCenter, _pendingSites.Keys);
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
        foreach (var site in _pendingSites.Where(kv => view.State.Tick - kv.Value.Tick > 40).Select(kv => kv.Key).ToList())
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

            var stats = view.Rules.Buildings.First(b => b.Type == step.Type);
            var prerequisitesMet = stats.Requires.All(r => view.MyBuildings.Any(b => b.Type == r && b.Completed));
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
                // Replacing something we lost, or the TechLab: the army waits until we can pay.
                if (_peak.GetValueOrDefault(step.Type) >= step.Count || step.Type == BuildingType.TechLab)
                {
                    _saving = true;
                }

                return;
            }

            var site = view.FindBuildSite(step.Site, _pendingSites.Keys);
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

    // ---- Upgrades: Harvest1, Weapons1, Armor1, then (after Barracks #3) Weapons2, Armor2 ----

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

            if (i >= TierOneCount && barracksDone < Math.Min(3, s.MaxBarracks))
            {
                return;   // tier 2 waits for the third Barracks
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

    private void TrainArmy(BotView view, BotSettings s, List<CommandRequest> commands, ref int budget)
    {
        var fighters = view.Army.Where(u => u.Type != UnitType.Scout).ToList();
        var archers = fighters.Count(u => u.Type == UnitType.Archer)
                      + view.MyBuildings.Sum(b => b.Production.Count(p => p.UnitType == UnitType.Archer));
        var total = fighters.Count
                    + view.MyBuildings.Sum(b => b.Production.Count(p => p.UnitType is UnitType.Soldier or UnitType.Archer));
        var rally = HomeRally(view);

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
        var depot = view.MyBuildings
            .Where(b => b.Type == BuildingType.ResourceDepot && b.Completed && Dist(b.X, b.Y, cc.X, cc.Y) > HomeRadius)
            .OrderBy(b => b.Id)
            .FirstOrDefault();
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

    private void CommandArmy(BotView view, BotSettings s, List<CommandRequest> commands)
    {
        var tick = view.State.Tick;
        var army = view.Army.Where(u => u.Type != UnitType.Scout).ToList();   // the raider scout is handled separately
        var cc = view.CommandCenter!;

        // 1. Forget the dead. Retire the wave once it's mostly gone; survivors become "home" units.
        _wave.IntersectWith(army.Select(u => u.Id));
        if (_waveSize > 0 && _wave.Count < Math.Max(2, _waveSize / 3))
        {
            _wave.Clear();
            _waveSize = 0;
        }

        // 2. Launch time? Everyone not in the wave except the garrison (the oldest DefendPercent of them) goes.
        var home = army.Where(u => !_wave.Contains(u.Id)).OrderBy(u => u.Id).ToList();
        var timeReady = s.AttackAtTick == 0 || tick >= s.AttackAtTick;
        if (_waveSize == 0 && timeReady && s.AttackThreshold > 0 && home.Count >= s.AttackThreshold)
        {
            var stay = (int)Math.Round(home.Count * s.DefendPercent / 100.0);
            _wave = home.Skip(stay).Select(u => u.Id).ToHashSet();
            _waveSize = _wave.Count;
            _lastRetargetTick = -RetargetEveryTicks;
            home = home.Take(stay).ToList();
            if (_waveSize > 0)
            {
                Console.WriteLine($"[tick {tick}] launching a wave of {_waveSize}, garrison {stay}");
            }
        }

        // 3. The wave marches.
        if (_wave.Count > 0 && view.AttackTarget() is { } target)
        {
            var retarget = tick - _lastRetargetTick >= RetargetEveryTicks;
            var ids = army
                .Where(u => _wave.Contains(u.Id) && (retarget || u.Activity == UnitActivity.Idle))
                .Select(u => u.Id)
                .ToArray();
            if (ids.Length > 0)
            {
                commands.Add(new CommandRequest { Type = CommandType.Attack, UnitIds = ids, X = target.X, Y = target.Y });
            }

            if (retarget)
            {
                _lastRetargetTick = tick;
            }
        }

        // 4. Home units. Once the far depot stands, only the garrison stays home; the rest guard the far field.
        var homeRally = HomeRally(view);
        var homeBuildings = view.MyBuildings.Where(b => Dist(b.X, b.Y, cc.X, cc.Y) <= HomeRadius).ToList();
        var homeThreat = NearestThreat(view, homeBuildings, cc.X, cc.Y);

        if (FarRally(view) is { } farRally)
        {
            var stay = (int)Math.Round(home.Count * s.DefendPercent / 100.0);
            var garrison = home.Take(stay).ToList();
            var field = home.Skip(stay).ToList();
            var fieldThreat = NearestThreat(view, view.MyBuildings, farRally.X, farRally.Y);

            Guard(commands, garrison, homeRally, homeThreat);
            Guard(commands, field, farRally, fieldThreat);
        }
        else
        {
            Guard(commands, home, homeRally, homeThreat ?? NearestThreat(view, view.MyBuildings, cc.X, cc.Y));
        }
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