using System.Reflection;
using FiveStack.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Translation;
using FiveStack.Entities;

namespace FiveStack;

[PluginMetadata(
    Id = "FiveStack",
    Version = "__RELEASE_VERSION__",
    Name = "5stack",
    Author = "5Stack.gg",
    Description = "5Stack creates and managements custom matches"
)]
public partial class FiveStackPlugin : BasePlugin
{
    private ILogger<FiveStackPlugin> _logger = null!;
    private ILocalizer _localizer = null!;
    private IServiceProvider _serviceProvider = null!;

    private SteamService _steamService = null!;
    private GameDemos _gameDemos = null!;
    private GameServer _gameServer = null!;
    private MatchEvents _matchEvents = null!;
    private MatchService _matchService = null!;
    private CaptainSystem _captainSystem = null!;
    private CoachSystem _coachSystem = null!;
    private RankSystem _rankSystem = null!;
    private SurrenderSystem _surrenderSystem = null!;
    private GameBackUpRounds _gameBackupRounds = null!;
    private EnvironmentService _environmentService = null!;

    private const long PingEveryMs = 15_000;
    private EventDelegates.OnWorldUpdate? _pingHandler;
    private long _lastPingMs;
    private Guid _chatHookId;
    private Guid _commandHookId;
    private EventDelegates.OnPrecacheResource? _precacheHandler;

    public FiveStackPlugin(ISwiftlyCore core)
        : base(core) { }

    private ISwiftlyCore _core => Core;

    public string ModuleVersion =>
        typeof(FiveStackPlugin).GetCustomAttribute<PluginMetadata>()?.Version ?? "unknown";

    public override void Load(bool hotReload)
    {
        Core.Configuration.InitializeJsonWithModel<FiveStackConfig>("config.jsonc", "FiveStack")
            .Configure(builder =>
            {
                builder.AddJsonFile("config.jsonc", optional: true, reloadOnChange: true);
            });

        // The manager is rooted at addons/swiftlys2/configs/plugins/FiveStack;
        // reading from the process CWD would miss the user's config file.
        IConfiguration configuration = Core.Configuration.Manager;

        ServiceCollection services = new();
        services
            .AddSwiftly(Core)
            .AddSingleton<IConfiguration>(configuration)
            .Configure<FiveStackConfig>(config =>
                configuration.GetSection("FiveStack").Bind(config)
            )
            .AddSingleton<EnvironmentService>()
            .AddSingleton<SteamService>()
            .AddSingleton<GameServer>()
            .AddSingleton<MatchService>()
            .AddSingleton<MatchEvents>()
            .AddSingleton<GameDemos>()
            .AddSingleton<GameBackUpRounds>()
            .AddSingleton<SurrenderSystem>()
            .AddSingleton<CoachSystem>()
            .AddSingleton<CaptainSystem>()
            .AddSingleton<RankSystem>()
            .AddTransient<MatchManager>()
            .AddTransient<VoteSystem>()
            .AddTransient<KnifeSystem>()
            .AddTransient<ReadySystem>()
            .AddTransient<TimeoutSystem>()
            .AddTransient<AutoCancelCountdownSystem>()
            .AddTransient<DisconnectBudgetSystem>()
            .AddTransient<TeamEmptyForfeitSystem>()
            .AddTransient<WarmupShortenSystem>();

        _serviceProvider = services.BuildServiceProvider();

        _logger = _serviceProvider.GetRequiredService<ILogger<FiveStackPlugin>>();
        _localizer = _serviceProvider.GetRequiredService<ILocalizer>();

        MatchUtility.Initialize(Core);
        TimerUtility.Initialize(Core);
        HibernationUtility.Initialize(Core);

        _environmentService = _serviceProvider.GetRequiredService<EnvironmentService>();
        _steamService = _serviceProvider.GetRequiredService<SteamService>();
        _gameServer = _serviceProvider.GetRequiredService<GameServer>();
        _matchService = _serviceProvider.GetRequiredService<MatchService>();
        _matchEvents = _serviceProvider.GetRequiredService<MatchEvents>();
        _gameDemos = _serviceProvider.GetRequiredService<GameDemos>();
        _gameBackupRounds = _serviceProvider.GetRequiredService<GameBackUpRounds>();
        _surrenderSystem = _serviceProvider.GetRequiredService<SurrenderSystem>();
        _coachSystem = _serviceProvider.GetRequiredService<CoachSystem>();
        _captainSystem = _serviceProvider.GetRequiredService<CaptainSystem>();
        _rankSystem = _serviceProvider.GetRequiredService<RankSystem>();

        _environmentService.Load();

        CommandUtility.Initialize(
            _environmentService.GetPublicChatTrigger(),
            _environmentService.GetSilentChatTrigger()
        );

        _logger.LogInformation($"Server ID: {_environmentService.GetServerId()}");

        InitializeConnectClientHook();

        try
        {
            ClanTagUtility.Hook(Core);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to hook SetClan, clan tags will flicker");
        }

        _rankSystem.Start();

        Core.Event.OnMapLoad += OnMapLoad;

        _precacheHandler = (@event) =>
        {
            @event.AddItem(ModelPathCtmSas);
            @event.AddItem(ModelPathTmPhoenix);
        };
        Core.Event.OnPrecacheResource += _precacheHandler;

        _chatHookId = Core.Command.HookClientChat(
            (playerId, text, teamonly) =>
            {
                IPlayer? player = Core.PlayerManager.GetPlayer(playerId);

                string message = text.Trim('"');

                HookResult result = HookResult.Continue;

                // Previously skipped entirely for team-only chat, so
                // say_team never reached DEAFCS at all -- OnPlayerChat
                // itself now branches on teamonly to route to the right
                // room (see PlayerChat.cs).
                HookResult chatResult = OnPlayerChat(player, message, teamonly);
                if (chatResult != HookResult.Continue)
                {
                    result = chatResult;
                }

                HookResult gagResult = GagPlayer(player, message, teamonly);
                if (gagResult != HookResult.Continue)
                {
                    result = gagResult;
                }

                return result;
            }
        );

        _commandHookId = Core.Command.HookClientCommand(
            (playerId, commandLine) =>
            {
                string[] args = commandLine.Split(' ');
                if (args.Length > 0 && args[0] == "jointeam")
                {
                    IPlayer? player = Core.PlayerManager.GetPlayer(playerId);
                    return HandleJoinTeam(player, args);
                }

                return HookResult.Continue;
            }
        );

        _ = _matchService.GetMatchConfigs();

        if (_environmentService.IsOfflineMode())
        {
            _matchService.GetMatchFromOffline();
        }
        else
        {
            _gameServer.Ping(ModuleVersion);
            _lastPingMs = Environment.TickCount64;

            // OnWorldUpdate keeps firing while the server hibernates; ticks and timers stop
            _pingHandler = PingOnWorldUpdate;
            Core.Event.OnWorldUpdate += _pingHandler;
        }
    }

    private void PingOnWorldUpdate()
    {
        long nowMs = Environment.TickCount64;

        if (!HibernationUtility.HeartbeatDue(nowMs, _lastPingMs, PingEveryMs))
        {
            return;
        }

        // Stamp first so a failing ping is retried in 15 s, not on every world update
        _lastPingMs = nowMs;

        try
        {
            _gameServer.Ping(ModuleVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ping failed");
        }
    }

    public override void Unload()
    {
        try
        {
            _rankSystem.Stop();
            Core.Event.OnMapLoad -= OnMapLoad;

            if (_precacheHandler != null)
            {
                Core.Event.OnPrecacheResource -= _precacheHandler;
            }

            if (_chatHookId != Guid.Empty)
            {
                Core.Command.UnhookClientChat(_chatHookId);
            }

            if (_commandHookId != Guid.Empty)
            {
                Core.Command.UnhookClientCommand(_commandHookId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unsubscribe hooks during unload");
        }

        UninstallConnectClientHook();

        if (_pingHandler != null)
        {
            Core.Event.OnWorldUpdate -= _pingHandler;
        }

        // SwiftlyS2 only tears a plugin down once Unload returns, and a hot
        // reload loads the new build either way: an exception here would leave
        // this instance's timers and handlers running beside the new one.
        try
        {
            ClanTagUtility.Unhook();

            _matchService.GetCurrentMatch()?.Reset();
            _matchService.StayAwake(false);

            _ = _matchEvents.Disconnect();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset match state during unload");
        }
        finally
        {
            TimerUtility.ClearAll();
            HibernationUtility.Shutdown();
        }
    }
}
