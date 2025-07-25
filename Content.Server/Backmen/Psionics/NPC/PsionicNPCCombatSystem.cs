using Content.Shared.Actions;
using Content.Server.NPC.Events;
using Content.Server.NPC.Components;
using Content.Shared.Actions.Components;
using Content.Shared.Backmen.Abilities.Psionics;
using Robust.Shared.Timing;

namespace Content.Server.Backmen.Psionics.NPC;

public sealed class PsionicNPCCombatSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NoosphericZapPowerComponent, NPCSteeringEvent>(ZapCombat);
    }

    private void ZapCombat(EntityUid uid, NoosphericZapPowerComponent component, ref NPCSteeringEvent args)
    {
        if(component.NoosphericZapPowerAction is null ||
           _actions.GetAction(component.NoosphericZapPowerAction.Value) is not {} action ||
           !TryComp<EntityTargetActionComponent>(action, out var skill) ||
           skill.Event is null)
            return;


        if (_actions.IsCooldownActive(action,_timing.CurTime))
            return;

        if (!TryComp<NPCRangedCombatComponent>(uid, out var combat))
            return;

        if (_actions.ValidateEntityTarget(uid, combat.Target,(action,skill)))
        {
            var ev = (EntityTargetActionEvent?) _actions.GetEvent(action);
            if (ev == null)
                return;

            ev.Performer = uid;
            ev.Target = combat.Target;

            _actions.PerformAction(uid, action, ev);
            args.Steering.CanSeek = false;
        }
    }
}

