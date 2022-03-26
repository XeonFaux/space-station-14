using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Server.Objectives.Interfaces;
using Content.Server.Players;
using Content.Server.Roles;
using Content.Server.Maps;
using Content.Server.Nuclear;
using Content.Server.Spawners.Components;
using Content.Server.Station;
using Content.Shared.CCVar;
using Content.Shared.Dataset;
using Content.Shared.GameTicking;
using Content.Shared.Roles;
using Content.Shared.Sound;
using Content.Shared.Station;
using Robust.Server.Player;
using Robust.Shared.Audio;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;
using Robust.Shared.Localization;
using Robust.Shared.Log;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server.GameTicking.Rules;

public sealed class NuclearRuleSystem : GameRuleSystem
{
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly StationSystem _stationSystem = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IObjectivesManager _objectivesManager = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;

    public override string Prototype => "Nuclear";

    private readonly SoundSpecifier _addedSound = new SoundPathSpecifier("/Audio/Misc/tatoralert.ogg");
    private readonly List<OperativeRole> _operatives = new ();

    private const string OperativePrototypeID = "Operative";

    public int TotalOperatives => _operatives.Count;

    public override void Initialize()
    {
        base.Initialize();
        
        SubscribeLocalEvent<LoadingMapsEvent>(LoadMaps);
        SubscribeLocalEvent<RoundStartAttemptEvent>(OnStartAttempt);
        SubscribeLocalEvent<RulePlayerSpawningEvent>(SpawnOperative);
        SubscribeLocalEvent<RulePlayerJobsAssignedEvent>(OnPlayersSpawned);
        SubscribeLocalEvent<RoundEndTextAppendEvent>(OnRoundEndText);
    }

    public override void Started()
    {
        // This seems silly, but I'll leave it.
        _chatManager.DispatchServerAnnouncement(Loc.GetString("rule-nuclear-added-announcement"));
    }

    public override void Ended()
    {
        _operatives.Clear();
    }


    private void LoadMaps(LoadingMapsEvent ev)
    {
        
        var mapName = _cfg.GetCVar(CCVars.NuclearMap);
        if (_prototypeManager.TryIndex<GameMapPrototype>(mapName, out var gameMap))
        {
            ev.Maps.Add(gameMap);
        }
        else
        {
            Logger.ErrorS("preset", "Failed getting map for Nuclear mode.");
        }
    } 

    private void OnStartAttempt(RoundStartAttemptEvent ev)
    {
        if (!Enabled)
            return;

        var minPlayers = _cfg.GetCVar(CCVars.NuclearMinPlayers);
        if (!ev.Forced && ev.Players.Length < minPlayers)
        {
            _chatManager.DispatchServerAnnouncement(Loc.GetString("nuclear-not-enough-ready-players", ("readyPlayersCount", ev.Players.Length), ("minimumPlayers", minPlayers)));
            ev.Cancel();
            return;
        }

        if (ev.Players.Length == 0)
        {
            _chatManager.DispatchServerAnnouncement(Loc.GetString("nuclear-no-one-ready"));
            ev.Cancel();
            return;
        }
    }
    
    private void SpawnOperative(RulePlayerSpawningEvent ev)
    {
        if (!Enabled)
            return;

        // Get config
        var mapName = _cfg.GetCVar(CCVars.NuclearMap);
        var minPlayers = _cfg.GetCVar(CCVars.NuclearMinPlayers);
        var minOperatives = _cfg.GetCVar(CCVars.NuclearMinOperatives);
        var maxOperatives = _cfg.GetCVar(CCVars.NuclearMaxOperatives);
        var playersPerOperative = _cfg.GetCVar(CCVars.NuclearPlayersPerOperative);
        var startingBalance = _cfg.GetCVar(CCVars.NuclearStartingBalance);
        
        // Get candidate list
        var prefList = new List<IPlayerSession>(ev.PlayerPool);
        
        foreach (var player in prefList)
        {
            if (!ev.Profiles.ContainsKey(player.UserId))
            {
                prefList.Remove(player);
                continue;
            }
            if (!ev.Profiles[player.UserId].AntagPreferences.Contains(OperativePrototypeID))
            {
                prefList.Remove(player);
            }
        }
        
        // Choose operatives
        var numOperatives = MathHelper.Clamp(ev.PlayerPool.Count / playersPerOperative, minOperatives, maxOperatives);
        
        for (var i = 0; i < numOperatives; i++)
        {
            IPlayerSession operative;
            if (prefList.Count == 0)
            {
                if (ev.PlayerPool.Count == 0)
                {
                    Logger.InfoS("preset", "Insufficient ready players to fill up operatives, stopping the selection.");
                    break;
                }
                operative = _random.PickAndTake(ev.PlayerPool);
                Logger.InfoS("preset", "Insufficient preferred operatives, picking at random.");
            }
            else
            {
                operative = _random.PickAndTake(prefList);
                ev.PlayerPool.Remove(operative);
                Logger.InfoS("preset", "Selected a preferred operative.");
            }
            
            var mind = operative.Data.ContentData()?.Mind;
            if (mind == null)
            {
                Logger.ErrorS("preset", "Failed getting mind for picked operative.");
                continue;
            }
            
            var antagPrototype = _prototypeManager.Index<AntagPrototype>(OperativePrototypeID);
            var operativeRole = new OperativeRole(mind, antagPrototype);
            mind.AddRole(operativeRole);
            _operatives.Add(operativeRole);
        }

        // Find Syndicate Station
        var foundStation = new KeyValuePair<StationId, StationSystem.StationInfoData>();
        var stations = _stationSystem.StationInfo.ToList();
        foreach (var station in stations)
        {
            if (station.Value.Name == mapName)
            {
                foundStation = station;
            }
        }
        
        if (foundStation.Key == StationId.Invalid)
        {
            Logger.ErrorS("preset", "Failed finding station for Nuclear mode.");
        }
        
        // Spawn Operatives
        foreach (var operative in _operatives)
        {
            var player = operative.Mind?.Session;
            if (player == null)
            {
                Logger.ErrorS("preset", "Unable to find operative session.");
                continue;
            }

            GameTicker.PlayerJoinGame(player);
            
            var mind = player.ContentData()?.Mind;
            DebugTools.AssertNotNull(mind);
            
            //var mob = GameTicker.SpawnPlayerMob(OperativeRole, character, station, false);
            //mind.TransferTo(mob);
        }
        
        // Pick Commander
        // Generate Group name
        // Assign ID w/ balance
    }

    private void OnPlayersSpawned(RulePlayerJobsAssignedEvent ev)
    {
        var codewordCount = _cfg.GetCVar(CCVars.NuclearCodewordCount);

        // Generate Codewords 
        var adjectives = _prototypeManager.Index<DatasetPrototype>("adjectives").Values;
        var verbs = _prototypeManager.Index<DatasetPrototype>("verbs").Values;

        var codewordPool = adjectives.Concat(verbs).ToList();
        var finalCodewordCount = Math.Min(codewordCount, codewordPool.Count);
        var codewords = new string[finalCodewordCount];
        for (var i = 0; i < finalCodewordCount; i++)
        {
            codewords[i] = _random.PickAndTake(codewordPool);
        }

        // Give Operatives their codewords to keep in their character info menu
        foreach (var operative in _operatives)
        {
            operative.GreetOperative(codewords);

            operative.Mind.Briefing = Loc.GetString("operative-role-codewords", ("codewords", string.Join(", ", codewords)));
        }

        SoundSystem.Play(Filter.Empty().AddWhere(s => ((IPlayerSession)s).Data.ContentData()?.Mind?.HasRole<OperativeRole>() ?? false), _addedSound.GetSound(), AudioParams.Default);
    }

    private void OnRoundEndText(RoundEndTextAppendEvent ev)
    {
        if (!Enabled)
            return;

        /*var result = Loc.GetString("nuclear-round-end-result", ("operativeCount", _operatives.Count));

        foreach (var operative in _operatives)
        {
            var name = operative.Mind.CharacterName;
            traitor.Mind.TryGetSession(out var session);
            var username = session?.Name;

            var objectives = traitor.Mind.AllObjectives.ToArray();
            if (objectives.Length == 0)
            {
                if (username != null)
                {
                    if (name == null)
                        result += "\n" + Loc.GetString("traitor-user-was-a-traitor", ("user", username));
                    else
                        result += "\n" + Loc.GetString("traitor-user-was-a-traitor-named", ("user", username), ("name", name));
                }
                else if (name != null)
                    result += "\n" + Loc.GetString("traitor-was-a-traitor-named", ("name", name));

                continue;
            }

            if (username != null)
            {
                if (name == null)
                    result += "\n" + Loc.GetString("traitor-user-was-a-traitor-with-objectives", ("user", username));
                else
                    result += "\n" + Loc.GetString("traitor-user-was-a-traitor-with-objectives-named", ("user", username), ("name", name));
            }
            else if (name != null)
                result += "\n" + Loc.GetString("traitor-was-a-traitor-with-objectives-named", ("name", name));

            foreach (var objectiveGroup in objectives.GroupBy(o => o.Prototype.Issuer))
            {
                result += "\n" + Loc.GetString($"preset-traitor-objective-issuer-{objectiveGroup.Key}");

                foreach (var objective in objectiveGroup)
                {
                    foreach (var condition in objective.Conditions)
                    {
                        var progress = condition.Progress;
                        if (progress > 0.99f)
                        {
                            result += "\n- " + Loc.GetString(
                                "traitor-objective-condition-success",
                                ("condition", condition.Title),
                                ("markupColor", "green")
                            );
                        }
                        else
                        {
                            result += "\n- " + Loc.GetString(
                                "traitor-objective-condition-fail",
                                ("condition", condition.Title),
                                ("progress", (int) (progress * 100)),
                                ("markupColor", "red")
                            );
                        }
                    }
                }
            }
        }

        ev.AddLine(result);*/
    }
}
