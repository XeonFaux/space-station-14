using Content.Server.Chat.Managers;
using Content.Server.Roles;
using Content.Shared.Roles;
using Robust.Shared.IoC;
using Robust.Shared.Localization;

namespace Content.Server.Nuclear
{
    public sealed class OperativeRole : Role
    {
        public AntagPrototype Prototype { get; }

        public OperativeRole(Mind.Mind mind, AntagPrototype antagPrototype, bool isCommander = false) : base(mind)
        {
            Prototype = antagPrototype;
            Name = antagPrototype.Name;
            Antagonist = antagPrototype.Antagonist;
            IsCommander = isCommander;
        }

        public override string Name { get; }
        public override bool Antagonist { get; }

        [ViewVariables]
        public string? StartingGear => Prototype.StartingGear;

        public bool IsCommander { get; set; }

        public void GreetOperative(string[] codewords)
        {
            if (Mind.TryGetSession(out var session))
            {
                var chatMgr = IoCManager.Resolve<IChatManager>();
                chatMgr.DispatchServerMessage(session, Loc.GetString("operative-role-greeting"));
                chatMgr.DispatchServerMessage(session, Loc.GetString("operative-role-codewords", ("codewords", string.Join(", ",codewords))));
            }
        }
    }
}