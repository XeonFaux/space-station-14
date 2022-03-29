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
    
    private string SyndicateName;
    private string AgentTitle;
    private string CommanderTitle;
    private string FamilyName;

    public override void Initialize()
    {
        base.Initialize();
        
        SubscribeLocalEvent<LoadingMapsEvent>(LoadMaps);
        SubscribeLocalEvent<RoundStartAttemptEvent>(OnStartAttempt);
        SubscribeLocalEvent<RulePlayerSpawningEvent>(SpawningOverride);
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
    
    private void SpawningOverride(RulePlayerSpawningEvent ev)
    {
        if (!Enabled)
            return;

        // Get config
        var mapName = _cfg.GetCVar(CCVars.NuclearMap);
        var startingBalance = _cfg.GetCVar(CCVars.NuclearStartingBalance);
        
        if (!PickOperatives(ev))
        {
            Logger.ErrorS("preset", "Failed to find Operatives for Nuke Ops");
        }
        
        
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
    
    private bool SpawnOperatives()
    {
        foreach (var operative in _operatives)
        {
            var mind = operative.Mind?;
            if (mind == null)
            {
                Logger.ErrorS("preset", "Unable to find operative mind.");
                continue;
            }
            
            var session = mind.Session;
            GameTicker.PlayerJoinGame(session);
            
            var spawnPoint = GetSpawnPoint(GetStation());
            var entity = EntityManager.SpawnEntity(
                _prototypeManager.Index<SpeciesPrototype>(profile?.Species ?? SpeciesManager.DefaultSpecies).Prototype,
                spawnPoint);
            
            var startingGear
_prototypeManager.Index<StartingGearPrototype»(operative.StartingGear);
            EquipStartingGear(entity, startingGear, profile);
            
            _humanoidAppearanceSystem.UpdateFromProfile(entity,
profile);
            EntityManager.GetComponent<MetaDataComponent>(entity).EntityName
profile.Name;

        }
    } 
    
    // possibly return point itself instead of coords
    private EntityCoordinates GetSpawnPoint(StationId station)
    {
        EntityCoordinates spawnPoint;
        List<EntityCoordinates> _possiblePositions = new();

        foreach (var (point, transform) in EntityManager.EntityQuery<SpawnPointComponent, TransformComponent>(true))
        {
            var matchingStation =
                    EntityManager.TryGetComponent<StationComponent>(transform.ParentUid, out var stationComponent) &&
                    stationComponent.Station == station;
                DebugTools.Assert(EntityManager.TryGetComponent<IMapGridComponent>(transform.ParentUid, out _));

            if (point.SpawnType == SpawnPointType.Antag && matchingStation)
                    _possiblePositions.Add(transform.Coordinates);
        }

        if (_possiblePositions.Count != 0)
            spawnPoint = _robustRandom.Pick(_possiblePositions);

        return spawnPoint;
    }
    
    private KeyValuePair<StationId, StationSystem.StationInfoData> GetStation()
    {
        KeyValuePair<StationId, StationSystem.StationInfoData> foundStation = new ();
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
        
        return foundStation;
    } 
    
    private bool PickOperatives(RulePlayerSpawningEvent ev)
    {
        var minPlayers = _cfg.GetCVar(CCVars.NuclearMinPlayers);
        var minOperatives = _cfg.GetCVar(CCVars.NuclearMinOperatives);
        var maxOperatives = _cfg.GetCVar(CCVars.NuclearMaxOperatives);
        var playersPerOperative = _cfg.GetCVar(CCVars.NuclearPlayersPerOperative);
        
        var prefList = new List<IPlayerSession>(ev.PlayerPool);
        var backList = new List<IPlayerSession>(ev.PlayerPool);
        
        foreach (var player in prefList)
        {
            // Not a player
            if (!ev.Profiles.ContainsKey(player.UserId))
            {
                prefList.Remove(player);
                backList.Remove(player);
                continue;
            }
            // Not connected
            if (player.Data.ContentData()?.Mind == null)
            {
                prefList.Remove(player);
                backList.Remove(player);
                continue;
            
            // Does not have Antag preference on
            if (!ev.Profiles[player.UserId].AntagPreferences.Contains(OperativePrototypeID))
            {
                prefList.Remove(player);
            }
        }
        
        // Choose operatives
        var numOperatives = MathHelper.Clamp(ev.PlayerPool.Count / playersPerOperative, minOperatives, maxOperatives);
        
        while (TotalOperatives < numOperatives)
        {
            IPlayerSession operative;
            if (prefList.Count == 0)
            {
                if (backList.Count == 0)
                {
                    if (TotalOperatives > minOperatives)
                    {
                        Logger.InfoS("preset", $"Only {TotalOperatives}/{numOperatives} Operatives were found. Stopping search.");
                        break;
                    }
                    else
                    {
                        Logger.InfoS("preset", "Insufficient ready players to fill up operatives, stopping the selection.");
                        return false;
                    }
                }
                operative = _random.PickAndTake(backList);
                Logger.InfoS("preset", $"Operative #{TotalOperatives + 1}: Random, due to insufficient preferred operatives.");
            }
            else
            {
                operative = _random.PickAndTake(prefList);
                Logger.InfoS("preset", $"Operative #{TotalOperatives + 1}: Preferred");
            }
            
            // Remove from spawning pool
            ev.PlayerPool.Remove(operative);
            
            var mind = operative.Data.ContentData()?.Mind:
            
            // Give role 
            var antagPrototype = _prototypeManager.Index<AntagPrototype>(OperativePrototypeID);
            var operativeRole = new OperativeRole(mind, antagPrototype);
            mind.AddRole(operativeRole);
            _operatives.Add(operativeRole);
        }
        
        // Pick Commander
        // Eventually gonna make this transfer over to another operative on commander death, copy access/codes. Make it a function in OperativeRole
        _random.Pick(_operatives).IsCommander = true;
        return true;
    }

#region Name Generation

    private string GenerateSyndicateName()
    {
        if (SyndicateName.IsNullOrEmpty())
        {
            // Need to allow Holiday names in the future. 
            var groupFirstName = _random.Pick(_prototypeManager.Index<DatasetPrototype>("first_names_nuclear"));
            var groupLastName = _random.Pick(_prototypeManager.Index<DatasetPrototype>("last_names_nuclear"));
        
            SyndicateName = new string($"{groupFirstName} {groupLastName}");
        }
        
        return SyndicateName
    }
    
    private string GenerateAgentTitle()
    {
        if (AgentTitle.IsNullOrEmpty())
        {
            var title = _random.Pick(_prototypeManager.Index<DatasetPrototype>("agent_title_nuclear"));
        
            AgentTitle = new string($"{title}");
        }
        
        return AgentTitle;
    }
    
    private string GenerateCommanderTitle()
    {
        if (CommanderTitle.IsNullOrEmpty())
        {
            var title = _random.Pick(_prototypeManager.Index<DatasetPrototype>("commander_title_nuclear"));
        
            CommanderTitle = new string($"{title}");
        }
        
        return CommanderTitle;
    }
    
    private string GetOfficialName(OperativeRole operative)
    {
        string syndicateName = GenerateSyndicateName();
        string title = operative.IsCommander ? GenerateCommanderTitle() : GenerateAgentTitle();
        string lastName;
        
        return new string($"{syndicateName} {title} {lastName}")
    }
    
#endregion 
}
