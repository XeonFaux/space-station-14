using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.Chat.Managers;
using Content.Server.Objectives.Interfaces;
using Content.Server.Players;
using Content.Server.Roles;
using Content.Server.Traitor;
using Content.Server.Traitor.Uplink;
using Content.Server.Traitor.Uplink.Account;
using Content.Shared.CCVar;
using Content.Shared.Dataset;
using Content.Shared.Roles;
using Content.Shared.Sound;
using Content.Shared.Traitor.Uplink;
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

        SubscribeLocalEvent<RoundStartAttemptEvent>(OnStartAttempt);
        SubscribeLocalEvent<RulePlayerJobsAssignedEvent>(OnPlayersSpawned);
        SubscribeLocalEvent<RoundEndTextAppendEvent>(OnRoundEndText);
    }

    public override void Started()
    {
        // This seems silly, but I'll leave it.
        _chatManager.DispatchServerAnnouncement(Loc.GetString("rule-traitor-added-announcement"));
    }

    public override void Ended()
    {
        _operatives.Clear();
    }

    private void OnStartAttempt(RoundStartAttemptEvent ev)
    {
        if (!Enabled)
            return;

        var minPlayers = _cfg.GetCVar(CCVars.NuclearMinPlayers);
        if (!ev.Forced && ev.Players.Length < minPlayers)
        {
            _chatManager.DispatchServerAnnouncement(Loc.GetString("traitor-not-enough-ready-players", ("readyPlayersCount", ev.Players.Length), ("minimumPlayers", minPlayers)));
            ev.Cancel();
            return;
        }

        if (ev.Players.Length == 0)
        {
            _chatManager.DispatchServerAnnouncement(Loc.GetString("traitor-no-one-ready"));
            ev.Cancel();
            return;
        }
    }

    private void OnPlayersSpawned(RulePlayerJobsAssignedEvent ev)
    {
        if (!Enabled)
            return;

        var operativesPerPlayers = _cfg.GetCVar(CCVars.OperativesPerPlayers);
        var maxOperatives = _cfg.GetCVar(CCVars.NuclearMaxOperatives);
        var minOperatives = _cfg.GetCVar(CCVars.NuclearMinOperatives);
        var codewordCount = _cfg.GetCVar(CCVars.NuclearCodewordCount);
        var startingBalance = _cfg.GetCVar(CCVars.NuclearStartingBalance);

        var list = new List<IPlayerSession>(ev.Players).Where(x =>
            x.Data.ContentData()?.Mind?.AllRoles.All(role => role is not Job {CanBeAntag: false}) ?? false
        ).ToList();

        var prefList = new List<IPlayerSession>();

        foreach (var player in list)
        {
            if (!ev.Profiles.ContainsKey(player.UserId))
            {
                continue;
            }
            var profile = ev.Profiles[player.UserId];
            if (profile.AntagPreferences.Contains(OperativePrototypeID))
            {
                prefList.Add(player);
            }
        }

        var numOperatives = MathHelper.Clamp(ev.Players.Length / operativesPerPlayers,
            minOperatives, maxOperatives);

        for (var i = 0; i < numOperatives; i++)
        {
            IPlayerSession operative;
            if(prefList.Count == 0)
            {
                if (list.Count == 0)
                {
                    Logger.InfoS("preset", "Insufficient ready players to fill up operatives, stopping the selection.");
                    break;
                }
                operative = _random.PickAndTake(list);
                Logger.InfoS("preset", "Insufficient preferred operatives, picking at random.");
            }
            else
            {
                operative = _random.PickAndTake(prefList);
                list.Remove(operative);
                Logger.InfoS("preset", "Selected a preferred operative.");
            }
            var mind = traitor.Data.ContentData()?.Mind;
            if (mind == null)
            {
                Logger.ErrorS("preset", "Failed getting mind for picked operative.");
                continue;
            }

            // Give Agent card with telecrystals
            DebugTools.AssertNotNull(mind.OwnedEntity);

            /*var uplinkAccount = new UplinkAccount(startingBalance, mind.OwnedEntity!);
            var accounts = EntityManager.EntitySysManager.GetEntitySystem<UplinkAccountsSystem>();
            accounts.AddNewAccount(uplinkAccount);

            if (!EntityManager.EntitySysManager.GetEntitySystem<UplinkSystem>()
                    .AddUplink(mind.OwnedEntity!.Value, uplinkAccount))
                continue;*/

            var antagPrototype = _prototypeManager.Index<AntagPrototype>(OperativePrototypeID);
            var operativeRole = new OperativeRole(mind, antagPrototype);
            mind.AddRole(operativeRole);
            _traitors.Add(operativeRole);
        }

        var adjectives = _prototypeManager.Index<DatasetPrototype>("adjectives").Values;
        var verbs = _prototypeManager.Index<DatasetPrototype>("verbs").Values;

        var codewordPool = adjectives.Concat(verbs).ToList();
        var finalCodewordCount = Math.Min(codewordCount, codewordPool.Count);
        var codewords = new string[finalCodewordCount];
        for (var i = 0; i < finalCodewordCount; i++)
        {
            codewords[i] = _random.PickAndTake(codewordPool);
        }

        foreach (var traitor in _traitors)
        {
            traitor.GreetTraitor(codewords);

            //give traitors their objectives
            var difficulty = 0f;
            for (var pick = 0; pick < maxPicks && maxDifficulty > difficulty; pick++)
            {
                var objective = _objectivesManager.GetRandomObjective(traitor.Mind);
                if (objective == null) continue;
                if (traitor.Mind.TryAddObjective(objective))
                    difficulty += objective.Difficulty;
            }

            //give traitors their codewords to keep in their character info menu
            traitor.Mind.Briefing = Loc.GetString("traitor-role-codewords", ("codewords", string.Join(", ",codewords)));
        }

        SoundSystem.Play(Filter.Empty().AddWhere(s => ((IPlayerSession)s).Data.ContentData()?.Mind?.HasRole<TraitorRole>() ?? false), _addedSound.GetSound(), AudioParams.Default);
    }

    private void OnRoundEndText(RoundEndTextAppendEvent ev)
    {
        if (!Enabled)
            return;

        var result = Loc.GetString("traitor-round-end-result", ("traitorCount", _traitors.Count));

        foreach (var traitor in _traitors)
        {
            var name = traitor.Mind.CharacterName;
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

        ev.AddLine(result);
    }
}
