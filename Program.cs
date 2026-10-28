using System.ComponentModel;
using NetRts.Bots;
using NetRts.Protocol;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<BattleControl>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();



app.MapGet("/register", async() =>
{
    var key = File.ReadAllText(@"..\NetRTS\badger-bot\apikey-Mike.txt").Trim();
    using var client = new NetRtsClient(new Uri("https://NetRTS.snowse.io"), key);
    var me = await client.GetMeAsync();
    return $"Connected as {me.Name}: {me.Wins} wins, {me.Losses} losses (rating {me.Rating})";
});



app.MapGet("/api/settings", (SettingsStore store) => store.Current);

app.MapPost("/api/attack", (BattleControl control) =>
{
    control.RequestAttack();
    Console.WriteLine($"ATTACK button pressed (bot status: {(control.Status is { } st ? "running at tick " + st.Tick : "NO BOT RUNNING IN THIS SERVER")})");
    return Results.Ok(new { botRunning = control.Status is not null, tick = control.Status?.Tick });
});

app.MapPost("/api/recall", (BattleControl control) =>
{
    control.RequestRecall();
    return Results.Ok();
});

app.MapGet("/api/status", (BattleControl control) => control.Status is { } s ? Results.Ok(s) : Results.NoContent());

app.MapPost("/api/settings", (BotSettings incoming, SettingsStore store) =>
{
    store.Update(incoming.Clamped());
    return Results.Ok(store.Current);
});

app.MapPost("/api/join", async (JoinRequest req, SettingsStore store, BattleControl control) =>
{
    var key = File.ReadAllText(@"..\NetRTS\badger-bot\apikey-Mike.txt").Trim();
    var client = new NetRtsClient(new Uri("https://NetRTS.snowse.io"), key);
    
    MatchSummaryDto match;
    try
    {
        var named = await client.GetMatchByNameAsync(req.MatchName);
        match = await client.JoinMatchAsync(named.MatchId);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var bot = new TunableBot(store, control);
    _ = Task.Run(async () =>
    {
        try
        {
            await client.WaitForStartAsync(match.MatchId);   // waits until the admin starts the heats
            await BotLoop.RunAsync(client, match.MatchId, bot, Console.WriteLine);
        }
        catch (Exception ex)
        {
            Console.WriteLine("League bot failed: " + ex.Message);
        }
    });
    
    store.Lock();
    return Results.Ok(new { matchId = match.MatchId });
});
app.MapPost("/api/start", async (StartRequest req, SettingsStore store, BattleControl control) =>
{
    string[] allowed = ["sitter", "rusher", "balanced", "economist"];
    // "melee" = three different house bots (4 armies on the map); "brawl" = three rushers; otherwise one opponent.
    string[] opponents = req.Opponent switch
    {
        "melee" => ["rusher", "balanced", "economist"],
        "brawl" => ["rusher", "rusher", "rusher"],
        _ when allowed.Contains(req.Opponent) => [req.Opponent!],
        _ => ["balanced"],
    };
    var key = File.ReadAllText(@"..\NetRTS\badger-bot\apikey-Mike.txt").Trim();
    var client = new NetRtsClient(new Uri("https://NetRTS.snowse.io"), key);

    var match = await client.CreateMatchAsync(new CreateMatchRequest
    {
        MaxPlayers = opponents.Length + 1,
        HouseBots = opponents,
        Settings = new MatchSettingsDto { TickIntervalMs = 500 }
    });

    var bot = new TunableBot(store, control);
    _ = Task.Run(async () =>
    {
        try
        {
            await BotLoop.RunAsync(client, match.MatchId, bot, Console.WriteLine);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Bot loop failed: " + ex.Message);
        }
    });

    return new { matchId = match.MatchId };
});
app.Run();

record StartRequest(string? Opponent);
record JoinRequest(string MatchName);
