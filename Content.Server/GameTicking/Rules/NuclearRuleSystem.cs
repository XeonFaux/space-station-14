using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.Chat.Managers;
using Content.Server.CharacterAppearance.Systems;
using Content.Server.GameTicking;
using Content.Server.Objectives.Interfaces;
using Content.Server.Players;
using Content.Server.Roles;
using Content.Server.Maps;
using Content.Server.Nuclear;
using Content.Server.Preferences.Managers;
using Content.Server.Spawners.Components;
using Content.Server.Station;
using Content.Shared.CCVar;
using Content.Shared.Dataset;
using Content.Shared.GameTicking;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Content.Shared.Sound;
using Content.Shared.Species;
using Content.Shared.Station;
using Content.Shared.Random.Helpers;
using Robust.Server.Player;
using Robust.Shared.Audio;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;
using Robust.Shared.Localization;
using Robust.Shared.Log;
using Robust.Shared.Map;
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
    [Dependency] private readonly IServerPreferencesManager _prefsManager = default!;
    [Dependency] private readonly HumanoidAppearanceSystem _humanoidAppearanceSystem = default!;

    public override string Prototype => "Nuclear";

    private readonly SoundSpecifier _addedSound = new SoundPathSpecifier("/Audio/Misc/tatoralert.ogg");
    private readonly List<OperativeRole> _operatives = new ();

    private const string OperativePrototypeID = "Operative";

    public int TotalOperatives => _operatives.Count;
    
    private string SyndicateName;
    private string AgentTitle;
    private string CommanderTitle;
    private string FamilyName;
    
    private string MapName;


    /*
        // To-Do
        // GetSpawnPoint(): Get specific Antagonist spawn point (Commander & Agent spawn points)
        // OnRoundEndText(): Generate round summary text for objectives and nuke
        // Name Generation: Add generation of Holiday names
        // Generate team objectives besides nuke disk / detonate
        // Make Access/Authentication codes transfer to another Operative on Commander's death. Make choosing a Commander a function in OperativeRole
        // Add capability of multiple maps for operative spawns (Maybe in the future operatives start on different ships or team is split up.)
        // Add post-spawning uplink setup
    */


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
        _chatManager.DispatchServerAnnouncement(Loc.GetString("rule-nuclear-added-announcement"));
    }

    public override void Ended()
    {
        _operatives.Clear();
    }

    /// <summary>
    ///     Called before maps are loaded. Add specific gamemode maps to the round map list to be loaded.
    /// </summary>
    private void LoadMaps(LoadingMapsEvent ev)
    {    
        MapName = _cfg.GetCVar(CCVars.NuclearMap);
        if (_prototypeManager.TryIndex<GameMapPrototype>(MapName, out var gameMap))
        {
            ev.Maps.Add(gameMap);
        }
        else
        {
            Logger.ErrorS("preset", "Failed getting map for Nuclear mode.");
        }
    }
     
    /// <summary>
    ///     Called upon round start attenpt. If conditions for gamemode are not met, cancel the start attempt.
    /// </summary>
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
    
    /// <summary>
    ///     Called before players are spawned. Pull specific players out of the spawning queue and handle their spawning manually. Useful for spawning out-of-station antags.
    /// </summary>
    private void SpawningOverride(RulePlayerSpawningEvent ev)
    {
        if (!Enabled)
            return;

        // Get config
        var mapName = _cfg.GetCVar(CCVars.NuclearMap);
        var startingBalance = _cfg.GetCVar(CCVars.NuclearStartingBalance);
        
        var operatives = PickPlayers(ev);
        
        foreach (var operative in operatives)
        {
            // Check if player is still active
            var mind = operative.Data.ContentData()?.Mind;
            if (mind == null)
            {
                continue;
            }
            
            // Remove from spawning pool
            ev.PlayerPool.Remove(operative);
            
            // Give role 
            var antagPrototype = _prototypeManager.Index<AntagPrototype>(OperativePrototypeID);
            var operativeRole = new OperativeRole(mind, antagPrototype);
            mind.AddRole(operativeRole);
            _operatives.Add(operativeRole);
        }
        
        // Pick Commander
        _random.Pick(_operatives).IsCommander = true;

        var station = GetStation(mapName);
        
        SpawnOperatives(ev, station);
    }

    /// <summary>
    ///     Called after players are spawned. Adds useful antag notes.
    /// </summary>
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

    /// <summary>
    ///     Called upon round ending. Generates round end summary.
    /// </summary>
    private void OnRoundEndText(RoundEndTextAppendEvent ev)
    {
        if (!Enabled)
            return;

        var result = Loc.GetString("nuclear-round-end-result", ("operativeCount", _operatives.Count));
        
        // Show operatives 
        foreach (var operative in _operatives)
        {
            var name = operative.Mind.CharacterName;
            operative.Mind.TryGetSession(out var session);
            var username = session?.Name;

            if (username != null)
            {
                if (name != null)
                    result += "\n" + Loc.GetString("nuclear-user-was-an-operative-named", ("user", username), ("name", name));
                else
                    result += "\n" + Loc.GetString("nuclear-user-was-an-operative", ("user", username));
            }
            else if (name != null)
                result += "\n" + Loc.GetString("nuclear-was-an-operative-named", ("name", name));
        }
        
        ev.AddLine(result);
    }
    
#region Helper Functions

    // Adds an uplink to the operative
    /*private void addUplink(OperativeRole operative)
    {
        var uplinkAccount = new UplinkAccount(startingBalance, owned);
        var accounts = EntityManager.EntitySysManager.GetEntitySystem<UplinkAccountsSystem>();
        accounts.AddNewAccount(uplinkAccount);
    }*/

    // Spawns operatives and returns a list of Entity Ids.
    private List<EntityUid> SpawnOperatives(RulePlayerSpawningEvent ev, StationId station)
    {
        List<EntityUid> entities = new List<EntityUid>();

        foreach (var operative in _operatives)
        {
            var mind = operative.Mind;
            var session = mind.Session;

            if (session == null)
            {
                Logger.ErrorS("preset", "Unable to find operative session.");
                continue;
            }

            GameTicker.PlayerJoinGame(session);

            var profile = ev.Profiles[session.UserId].Value;
            var spawnPoint = GetSpawnPoint(station);
            var entity = EntityManager.SpawnEntity(
                _prototypeManager.Index<SpeciesPrototype>(profile?.Species ?? SpeciesManager.DefaultSpecies).Prototype,
                spawnPoint);

            if (operative.StartingGear != null)
            {
                var startingGear = _prototypeManager.Index<StartingGearPrototype>(operative.StartingGear);
                GameTicker.EquipStartingGear(entity, startingGear, profile);
            }

            if (profile != null)
            {
                _humanoidAppearanceSystem.UpdateFromProfile(entity, profile);
                EntityManager.GetComponent<MetaDataComponent>(entity).EntityName = profile.Name;
            }

            entities.Add(entity);
        }

        return entities;
    } 
    
    // Returns random antagonist spawn point for specified station.
    private EntityCoordinates GetSpawnPoint(StationId stationId)
    {
        EntityCoordinates spawnPoint = new EntityCoordinates();
        List<EntityCoordinates> possiblePositions = new();

        foreach (var (point, transform) in EntityManager.EntityQuery<SpawnPointComponent, TransformComponent>(true))
        {
            var matchingStation =
                    EntityManager.TryGetComponent<StationComponent>(transform.ParentUid, out var stationComponent) &&
                    stationComponent.Station == stationId;
                DebugTools.Assert(EntityManager.TryGetComponent<IMapGridComponent>(transform.ParentUid, out _));

            if (point.SpawnType == SpawnPointType.Antag && matchingStation)
                    possiblePositions.Add(transform.Coordinates);
        }

        if (possiblePositions.Count != 0)
            spawnPoint = _random.Pick(possiblePositions);

        return spawnPoint;
    }
    
    // Returns StationId for specified station name. 
    private StationId GetStation(string stationName)
    {
        KeyValuePair<StationId, StationSystem.StationInfoData> foundStation = new ();

        foreach (var station in _stationSystem.StationInfo.ToList();)
        {
            if (station.Value.Name == stationName)
            {
                foundStation = station;
            }
        }
        
        if (foundStation.Key == StationId.Invalid)
        {
            Logger.ErrorS("preset", $"(GetStation({stationName}) failed finding station.");
        }
        
        return foundStation.Key;
    } 
    
    // Returns a randomly chosen list of players for the antag positions
    private List<IPlayerSession> PickPlayers(RulePlayerSpawningEvent ev)
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
            }
            
            // Does not have Antag preference on
            if (!ev.Profiles[player.UserId].AntagPreferences.Contains(OperativePrototypeID))
            {
                prefList.Remove(player);
            }
        }
        
        var chosenList = new List<IPlayerSession>();
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
                        return null;
                    }
                }
                chosenList.Add(_random.PickAndTake(backList));
                Logger.InfoS("preset", $"Operative #{TotalOperatives + 1}: Random, due to insufficient preferred operatives.");
            }
            else
            {
                chosenList.Add(_random.PickAndTake(prefList));
                Logger.InfoS("preset", $"Operative #{TotalOperatives + 1}: Preferred");
            }
        }
        
        return chosenList;
    }

#region Name Generation

    // Returns Syndicate organization name (Ex. Bonk Corp)
    private string GenerateSyndicateName()
    {
        if (SyndicateName.IsNullOrEmpty())
        {
            var groupFirstName = _random.Pick(_prototypeManager.Index<DatasetPrototype>("first_names_nuclear"));
            var groupLastName = _random.Pick(_prototypeManager.Index<DatasetPrototype>("last_names_nuclear"));
        
            SyndicateName = new string($"{groupFirstName} {groupLastName}");
        }
        
        return SyndicateName
    }
    
    // Returns operative title prefix (Ex. Agent)
    private string GenerateAgentTitle()
    {
        if (AgentTitle.IsNullOrEmpty())
        {
            var title = _random.Pick(_prototypeManager.Index<DatasetPrototype>("agent_title_nuclear"));
        
            AgentTitle = new string($"{title}");
        }
        
        return AgentTitle;
    }
    
    // Returns commander title prefix (Ex. Commander)
    private string GenerateCommanderTitle()
    {
        if (CommanderTitle.IsNullOrEmpty())
        {
            var title = _random.Pick(_prototypeManager.Index<DatasetPrototype>("commander_title_nuclear"));
        
            CommanderTitle = new string($"{title}");
        }
        
        return CommanderTitle;
    }
    
    // Returns operative full name (Ex. Bonk Corp Agent Smith)
    private string GetOfficialName(OperativeRole operative)
    {
        string syndicateName = GenerateSyndicateName();
        string title = operative.IsCommander ? GenerateCommanderTitle() : GenerateAgentTitle();
        string lastName = _random.Pick(_prototypeManager.Index<DatasetPrototype>("names_last"));;
        
        return new string($"{syndicateName} {title} {lastName}")
    }
    
#endregion Name Generation
#endregion Helper Functions
}
