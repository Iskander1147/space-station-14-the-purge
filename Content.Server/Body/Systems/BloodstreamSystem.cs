using System.Linq;
using Content.Server.Body.Components;
using Content.Server.Fluids.EntitySystems;
using Content.Server.Popups;
using Content.Shared.Alert;
using Content.Shared.Backmen.CCVar;
using Content.Shared.Backmen.Surgery;
using Content.Shared.Backmen.Surgery.Consciousness;
using Content.Shared.Backmen.Surgery.Consciousness.Components;
using Content.Shared.Backmen.Surgery.Consciousness.Systems;
using Content.Shared.Backmen.Surgery.Traumas.Components;
using Content.Shared.Backmen.Surgery.Wounds;
using Content.Shared.Backmen.Surgery.Wounds.Components;
using Content.Shared.Backmen.Surgery.Wounds.Systems;
using Content.Shared.Body.Part;
using Content.Shared.Body.Events;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Damage;
using Content.Shared.Drunk;
using Content.Shared.EntityEffects.Effects;
using Content.Shared.FixedPoint;
using Content.Shared.Forensics;
using Content.Shared.Forensics.Components;
using Content.Shared.HealthExaminable;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.Rejuvenate;
using Content.Shared.Speech.EntitySystems;
using JetBrains.Annotations;
using Robust.Server.Audio;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Body.Systems;

public sealed class BloodstreamSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IRobustRandom _robustRandom = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly DamageableSystem _damageableSystem = default!;
    [Dependency] private readonly PopupSystem _popupSystem = default!;
    [Dependency] private readonly PuddleSystem _puddleSystem = default!;
    [Dependency] private readonly MobStateSystem _mobStateSystem = default!;
    [Dependency] private readonly SharedDrunkSystem _drunkSystem = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutionContainerSystem = default!;
    [Dependency] private readonly SharedStutteringSystem _stutteringSystem = default!;
    [Dependency] private readonly AlertsSystem _alertsSystem = default!;

    // backmen edit start
    [Dependency] private readonly IConfigurationManager _cfg = default!;

    [Dependency] private readonly ConsciousnessSystem _consciousness = default!;
    [Dependency] private readonly BodySystem _body = default!;
    [Dependency] private readonly WoundSystem _wound = default!;

    private float _bleedingSeverityTrade;
    private float _bleedsScalingTime;

    private EntityQuery<BleedInflicterComponent> _bleedsQuery;
    private EntityQuery<WoundableComponent> _woundableQuery;
    private EntityQuery<ConsciousnessComponent> _consciousnessQuery;
    // backmen edit end

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BloodstreamComponent, ComponentInit>(OnComponentInit);
        SubscribeLocalEvent<BloodstreamComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<BloodstreamComponent, EntityUnpausedEvent>(OnUnpaused);
        SubscribeLocalEvent<BloodstreamComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<BloodstreamComponent, HealthBeingExaminedEvent>(OnHealthBeingExamined);
        SubscribeLocalEvent<BloodstreamComponent, BeingGibbedEvent>(OnBeingGibbed);
        SubscribeLocalEvent<BloodstreamComponent, ApplyMetabolicMultiplierEvent>(OnApplyMetabolicMultiplier);
        SubscribeLocalEvent<BloodstreamComponent, ReactionAttemptEvent>(OnReactionAttempt);
        SubscribeLocalEvent<BloodstreamComponent, SolutionRelayEvent<ReactionAttemptEvent>>(OnReactionAttempt);
        SubscribeLocalEvent<BloodstreamComponent, RejuvenateEvent>(OnRejuvenate);
        SubscribeLocalEvent<BloodstreamComponent, GenerateDnaEvent>(OnDnaGenerated);

        // backmen edit start
        SubscribeLocalEvent<BleedInflicterComponent, WoundHealAttemptEvent>(OnWoundHealAttempt);
        SubscribeLocalEvent<BleedInflicterComponent, WoundChangedEvent>(OnWoundChanged);

        Subs.CVar(_cfg, CCVars.BleedingSeverityTrade, value => _bleedingSeverityTrade = value, true);
        Subs.CVar(_cfg, CCVars.BleedsScalingTime, value => _bleedsScalingTime = value, true);

        _bleedsQuery = GetEntityQuery<BleedInflicterComponent>();
        _woundableQuery = GetEntityQuery<WoundableComponent>();
        _consciousnessQuery = GetEntityQuery<ConsciousnessComponent>();
        // backmen edit end
    }

    private void OnMapInit(Entity<BloodstreamComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.NextUpdate = _gameTiming.CurTime + ent.Comp.UpdateInterval;
    }

    private void OnUnpaused(Entity<BloodstreamComponent> ent, ref EntityUnpausedEvent args)
    {
        ent.Comp.NextUpdate += args.PausedTime;
    }

    private void OnReactionAttempt(Entity<BloodstreamComponent> entity, ref ReactionAttemptEvent args)
    {
        if (args.Cancelled)
            return;

        foreach (var effect in args.Reaction.Effects)
        {
            switch (effect)
            {
                case CreateEntityReactionEffect: // Prevent entities from spawning in the bloodstream
                case AreaReactionEffect: // No spontaneous smoke or foam leaking out of blood vessels.
                    args.Cancelled = true;
                    return;
            }
        }

        // The area-reaction effect canceling is part of avoiding smoke-fork-bombs (create two smoke bombs, that when
        // ingested by mobs create more smoke). This also used to act as a rapid chemical-purge, because all the
        // reagents would get carried away by the smoke/foam. This does still work for the stomach (I guess people vomit
        // up the smoke or spawned entities?).

        // TODO apply organ damage instead of just blocking the reaction?
        // Having cheese-clots form in your veins can't be good for you.
    }

    private void OnReactionAttempt(Entity<BloodstreamComponent> entity, ref SolutionRelayEvent<ReactionAttemptEvent> args)
    {
        if (args.Name != entity.Comp.BloodSolutionName
            && args.Name != entity.Comp.ChemicalSolutionName
            && args.Name != entity.Comp.BloodTemporarySolutionName)
        {
            return;
        }

        OnReactionAttempt(entity, ref args.Event);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<BloodstreamComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out var bloodstream, out var meta))
        {
            if (Paused(uid, meta))
                continue;

            if (_gameTiming.CurTime < bloodstream.NextUpdate)
                continue;

            bloodstream.NextUpdate += bloodstream.UpdateInterval;

            if (!_solutionContainerSystem.ResolveSolution(uid, bloodstream.BloodSolutionName, ref bloodstream.BloodSolution, out var bloodSolution))
                continue;

            // Adds blood to their blood level if it is below the maximum; Blood regeneration. Must be alive.
            if (bloodSolution.Volume < bloodSolution.MaxVolume && !_mobStateSystem.IsDead(uid))
            {
                TryModifyBloodLevel(uid, bloodstream.BloodRefreshAmount, bloodstream);
            }

            // Removes blood from the bloodstream based on bleed amount (bleed rate)
            // as well as stop their bleeding to a certain extent.
            if (bloodstream.BleedAmount > 0)
            {
                // Blood is removed from the bloodstream at a 1-1 rate with the bleed amount
                TryModifyBloodLevel(uid, (-bloodstream.BleedAmount), bloodstream);
                // Bleed rate is reduced by the bleed reduction amount in the bloodstream component.
                TryModifyBleedAmount(uid, -bloodstream.BleedReductionAmount, bloodstream);
            }

            // deal bloodloss damage if their blood level is below a threshold.
            var bloodPercentage = GetBloodLevelPercentage(uid, bloodstream);
            if (bloodPercentage < bloodstream.BloodlossThreshold && !_mobStateSystem.IsDead(uid))
            {
                // bloodloss damage is based on the base value, and modified by how low your blood level is.
                var amt = bloodstream.BloodlossDamage / (0.1f + bloodPercentage);

                _damageableSystem.TryChangeDamage(uid, amt, ignoreResistances: false, interruptsDoAfters: false);

                // Apply dizziness as a symptom of bloodloss.
                // The effect is applied in a way that it will never be cleared without being healthy.
                // Multiplying by 2 is arbitrary but works for this case, it just prevents the time from running out
                _drunkSystem.TryApplyDrunkenness(
                    uid,
                    (float) bloodstream.UpdateInterval.TotalSeconds * 2,
                    applySlur: false);
                _stutteringSystem.DoStutter(uid, bloodstream.UpdateInterval * 2, refresh: false);

                // storing the drunk and stutter time so we can remove it independently of other effects additions
                bloodstream.StatusTime += bloodstream.UpdateInterval * 2;
            }
            else if (!_mobStateSystem.IsDead(uid))
            {
                // If they're healthy, we'll try and heal some bloodloss instead.
                _damageableSystem.TryChangeDamage(
                    uid,
                    bloodstream.BloodlossHealDamage * bloodPercentage,
                    ignoreResistances: true,
                    interruptsDoAfters: false);

                // Remove the drunk effect when healthy. Should only remove the amount of drunk and stutter added by low blood level
                _drunkSystem.TryRemoveDrunkenessTime(uid, bloodstream.StatusTime.TotalSeconds);
                _stutteringSystem.DoRemoveStutterTime(uid, bloodstream.StatusTime.TotalSeconds);
                // Reset the drunk and stutter time to zero
                bloodstream.StatusTime = TimeSpan.Zero;
            }

            if (!_consciousnessQuery.TryComp(uid, out var consciousness))
                continue;

            // backmen edit start
            if (!_consciousness.TryGetNerveSystem(uid, out var nerveSys, consciousness))
                continue;

            var total = FixedPoint2.Zero;
            foreach (var (bodyPart, _) in _body.GetBodyChildren(uid))
            {
                total = _wound.GetWoundableWoundsWithComp<BleedInflicterComponent>(bodyPart)
                        .Aggregate(total, (current, wound) => current + wound.Comp2.BleedingAmount);
            }

            // I am very sorry, my dear shitcoders. but for now, while bloodstream is still in the state it is;
            // I cannot properly override bleeding for woundable based entities. I am very sorry.
            // This is this, and that is that.
            bloodstream.BleedAmount = (float) total / 5f;
            if (bloodstream.BleedAmount == 0)
                _alertsSystem.ClearAlert(uid, bloodstream.BleedingAlert);
            else
            {
                var severity = (short) Math.Clamp(Math.Round(bloodstream.BleedAmount, MidpointRounding.ToZero), 0, 10);
                _alertsSystem.ShowAlert(uid, bloodstream.BleedingAlert, severity);
            }

            var lethalBloodlossPoint = bloodstream.BloodMaxVolume * bloodstream.LethalBloodlossThreshold;
            var bloodAmount = bloodstream.BloodSolution.Value.Comp.Solution.Volume;

            var missingBlood = 1 - (bloodAmount - lethalBloodlossPoint) / (bloodstream.BloodMaxVolume - lethalBloodlossPoint);
            var consciousnessDamage = -consciousness.Cap * missingBlood;

            if (!_consciousness.SetConsciousnessModifier(
                    uid,
                    nerveSys.Value,
                    consciousnessDamage,
                    identifier: "Bloodloss",
                    type: ConsciousnessModType.Pain))
            {
                _consciousness.AddConsciousnessModifier(
                    uid,
                    nerveSys.Value,
                    consciousnessDamage,
                    identifier: "Bloodloss",
                    type: ConsciousnessModType.Pain);
            }
        }

        var bleedsQuery = EntityQueryEnumerator<BleedInflicterComponent, MetaDataComponent>();
        while (bleedsQuery.MoveNext(out var ent, out var bleeds, out var meta))
        {
            if (Paused(ent, meta))
                continue;

            var canBleed = CanWoundBleed(ent, bleeds) && bleeds.BleedingAmount > 0;
            if (bleeds.IsBleeding != canBleed)
            {
                bleeds.IsBleeding = canBleed;
                Dirty(ent, bleeds);
            }

            if (!bleeds.IsBleeding)
                continue;

            var totalTime = bleeds.ScalingFinishesAt - bleeds.ScalingStartsAt;
            var currentTime = bleeds.ScalingFinishesAt - _gameTiming.CurTime;

            if (totalTime <= currentTime || bleeds.Scaling >= bleeds.ScalingLimit)
                continue;

            var newBleeds = FixedPoint2.Clamp(
                bleeds.ScalingLimit * ((totalTime - currentTime) / totalTime),
                0,
                bleeds.ScalingLimit);

            bleeds.Scaling = newBleeds;
            Dirty(ent, bleeds);
        }
        // backmen edit end
    }

    private void OnComponentInit(Entity<BloodstreamComponent> entity, ref ComponentInit args)
    {
        if (!_solutionContainerSystem.EnsureSolution(entity.Owner,
                entity.Comp.ChemicalSolutionName,
                out var chemicalSolution) ||
            !_solutionContainerSystem.EnsureSolution(entity.Owner,
                entity.Comp.BloodSolutionName,
                out var bloodSolution) ||
            !_solutionContainerSystem.EnsureSolution(entity.Owner,
                entity.Comp.BloodTemporarySolutionName,
                out var tempSolution))
            return;

        chemicalSolution.MaxVolume = entity.Comp.ChemicalMaxVolume;
        bloodSolution.MaxVolume = entity.Comp.BloodMaxVolume;
        tempSolution.MaxVolume = entity.Comp.BleedPuddleThreshold * 4; // give some leeway, for chemstream as well

        // Fill blood solution with BLOOD
        // The DNA string might not be initialized yet, but the reagent data gets updated in the GenerateDnaEvent subscription
        bloodSolution.AddReagent(new ReagentId(entity.Comp.BloodReagent, GetEntityBloodData(entity.Owner)), entity.Comp.BloodMaxVolume - bloodSolution.Volume);
    }

    private void OnDamageChanged(Entity<BloodstreamComponent> ent, ref DamageChangedEvent args)
    {
        if (args.DamageDelta is null || !args.DamageIncreased)
        {
            return;
        }

        // TODO probably cache this or something. humans get hurt a lot
        if (!_prototypeManager.TryIndex(ent.Comp.DamageBleedModifiers, out var modifiers))
            return;

        // some reagents may deal and heal different damage types in the same tick, which means DamageIncreased will be true
        // but we only want to consider the dealt damage when causing bleeding
        var damage = DamageSpecifier.GetPositive(args.DamageDelta);
        var bloodloss = DamageSpecifier.ApplyModifierSet(damage, modifiers);

        if (bloodloss.Empty)
            return;

        // Does the calculation of how much bleed rate should be added/removed, then applies it
        var oldBleedAmount = ent.Comp.BleedAmount;
        var total = bloodloss.GetTotal();
        var totalFloat = total.Float();
        TryModifyBleedAmount(ent, totalFloat, ent);

        // Critical hit. Causes target to lose blood, using the bleed rate modifier of the weapon, currently divided by 5
        // The crit chance is currently the bleed rate modifier divided by 25.
        // Higher damage weapons have a higher chance to crit!
        var prob = Math.Clamp(totalFloat / 25, 0, 1);
        if (totalFloat > 0 && _robustRandom.Prob(prob))
        {
            TryModifyBloodLevel(ent, -total / 5, ent);
            _audio.PlayPvs(ent.Comp.InstantBloodSound, ent);
        }

        // Heat damage will cauterize, causing the bleed rate to be reduced.
        else if (totalFloat <= ent.Comp.BloodHealedSoundThreshold && oldBleedAmount > 0)
        {
            // Magically, this damage has healed some bleeding, likely
            // because it's burn damage that cauterized their wounds.

            // We'll play a special sound and popup for feedback.
            _audio.PlayPvs(ent.Comp.BloodHealedSound, ent);
            _popupSystem.PopupEntity(Loc.GetString("bloodstream-component-wounds-cauterized"), ent, ent, PopupType.Medium);
        }
    }

    // backmen edit start
    private void OnWoundHealAttempt(EntityUid uid, BleedInflicterComponent component, ref WoundHealAttemptEvent args)
    {
        if (component.IsBleeding)
            args.Cancelled = true;
    }

    private void OnWoundChanged(EntityUid uid, BleedInflicterComponent component, ref WoundChangedEvent args)
    {
        if (args.Component.WoundSeverityPoint < component.SeverityThreshold)
        {
            var woundable = args.Component.HoldingWoundable;
            if (!_woundableQuery.TryComp(woundable, out var woundableComp)
                || !TryComp(woundable, out BodyPartComponent? bodyPart) || !bodyPart.Body.HasValue)
                return;

            var bodyEnt = bodyPart.Body.Value;
            var bloodstream = Comp<BloodstreamComponent>(bodyEnt);

            if (args.Delta <= bloodstream.BloodHealedSoundThreshold
                     && component.IsBleeding && component.CauterizedBy.Contains(args.Component.DamageType))
            {
                foreach (var wound in
                         _wound.GetWoundableWoundsWithComp<BleedInflicterComponent>(woundable, woundableComp))
                {
                    var bleeds = wound.Comp2;
                    if (!bleeds.IsBleeding)
                        continue;

                    if (!bleeds.CauterizedBy.Contains(args.Component.DamageType))
                        continue;

                    bleeds.BleedingAmountRaw = 0;
                    bleeds.SeverityPenalty = 0;
                    bleeds.Scaling = 0;

                    bleeds.IsBleeding = false;
                }

                _audio.PlayPvs(bloodstream.BloodHealedSound, bodyEnt);
                _popupSystem.PopupEntity(Loc.GetString("bloodstream-component-wounds-cauterized"), bodyEnt, bodyEnt, PopupType.Medium);
            }
        }
        else
        {
            if (!CanWoundBleed(uid, component)
                && component.BleedingAmount < component.BleedingAmountRaw * component.ScalingLimit / 2)
            {
                component.BleedingAmountRaw = 0;
                component.SeverityPenalty = 0;
                component.Scaling = 0;

                component.IsBleeding = false;
            }
            else
            {
                if (args.Delta < 0)
                    return;

                // TODO: Instant bloodloss isn't funny at all
                //var prob = Math.Clamp((float) args.Delta / 25, 0, 1);
                //if (args.Delta > 0 && _robustRandom.Prob(prob))
                //{
                //    var woundable = args.Component.HoldingWoundable;
                //    if (TryComp(woundable, out BodyPartComponent? bodyPart) && bodyPart.Body.HasValue)
                //    {
                //        var bodyEnt = bodyPart.Body.Value;
                //        var bloodstream = Comp<BloodstreamComponent>(bodyEnt);

                        // instant blood loss
                //        TryModifyBloodLevel(bodyEnt, (-args.Delta) / 15, bloodstream);
                //        _audio.PlayPvs(bloodstream.InstantBloodSound, bodyEnt);
                //    }
                //}

                var oldBleedsAmount = component.BleedingAmountRaw;
                component.BleedingAmountRaw = args.Component.WoundSeverityPoint * _bleedingSeverityTrade;

                var severityPenalty = component.BleedingAmountRaw - oldBleedsAmount / _bleedsScalingTime;
                component.SeverityPenalty += severityPenalty;

                if (component.IsBleeding)
                {
                    // Pump up the bleeding if hit again.
                    component.ScalingLimit += args.Delta * _bleedingSeverityTrade;
                }

                var formula = (float) (args.Component.WoundSeverityPoint / _bleedsScalingTime * component.ScalingSpeed);
                component.ScalingFinishesAt = _gameTiming.CurTime + TimeSpan.FromSeconds(formula);
                component.ScalingStartsAt = _gameTiming.CurTime;

                // wounds that BLEED will not HEAL.
                component.IsBleeding = true;
            }
        }

        Dirty(uid, component);
    }
    // backmene dit end

    /// <summary>
    ///     Shows text on health examine, based on bleed rate and blood level.
    /// </summary>
    private void OnHealthBeingExamined(Entity<BloodstreamComponent> ent, ref HealthBeingExaminedEvent args)
    {
        // Shows massively bleeding at 0.75x the max bleed rate.
        if (ent.Comp.BleedAmount > ent.Comp.MaxBleedAmount * 0.75f)
        {
            args.Message.PushNewline();
            args.Message.AddMarkupOrThrow(Loc.GetString("bloodstream-component-massive-bleeding", ("target", ent.Owner)));
        }
        // Shows bleeding message when bleeding above half the max rate, but less than massively.
        else if (ent.Comp.BleedAmount > ent.Comp.MaxBleedAmount * 0.5f)
        {
            args.Message.PushNewline();
            args.Message.AddMarkupOrThrow(Loc.GetString("bloodstream-component-strong-bleeding", ("target", ent.Owner)));
        }
        // Shows bleeding message when bleeding above 0.25x the max rate, but less than half the max.
        else if (ent.Comp.BleedAmount > ent.Comp.MaxBleedAmount * 0.25f)
        {
            args.Message.PushNewline();
            args.Message.AddMarkupOrThrow(Loc.GetString("bloodstream-component-bleeding", ("target", ent.Owner)));
        }
        // Shows bleeding message when bleeding below 0.25x the max cap
        else if (ent.Comp.BleedAmount > 0)
        {
            args.Message.PushNewline();
            args.Message.AddMarkupOrThrow(Loc.GetString("bloodstream-component-slight-bleeding", ("target", ent.Owner)));
        }

        // If the mob's blood level is below the damage threshhold, the pale message is added.
        if (GetBloodLevelPercentage(ent, ent) < ent.Comp.BloodlossThreshold)
        {
            args.Message.PushNewline();
            args.Message.AddMarkupOrThrow(Loc.GetString("bloodstream-component-looks-pale", ("target", ent.Owner)));
        }
    }

    private void OnBeingGibbed(Entity<BloodstreamComponent> ent, ref BeingGibbedEvent args)
    {
        SpillAllSolutions(ent, ent);
    }

    private void OnApplyMetabolicMultiplier(
        Entity<BloodstreamComponent> ent,
        ref ApplyMetabolicMultiplierEvent args)
    {
        // TODO REFACTOR THIS
        // This will slowly drift over time due to floating point errors.
        // Instead, raise an event with the base rates and allow modifiers to get applied to it.
        if (args.Apply)
        {
            ent.Comp.UpdateInterval *= args.Multiplier;
            return;
        }
        ent.Comp.UpdateInterval /= args.Multiplier;
    }

    private void OnRejuvenate(Entity<BloodstreamComponent> entity, ref RejuvenateEvent args)
    {
        TryModifyBleedAmount(entity.Owner, -entity.Comp.BleedAmount, entity.Comp);

        if (_solutionContainerSystem.ResolveSolution(entity.Owner, entity.Comp.BloodSolutionName, ref entity.Comp.BloodSolution, out var bloodSolution))
            TryModifyBloodLevel(entity.Owner, bloodSolution.AvailableVolume, entity.Comp);

        if (_solutionContainerSystem.ResolveSolution(entity.Owner, entity.Comp.ChemicalSolutionName, ref entity.Comp.ChemicalSolution))
            _solutionContainerSystem.RemoveAllSolution(entity.Comp.ChemicalSolution.Value);
    }

    /// <summary>
    ///     Attempt to transfer provided solution to internal solution.
    /// </summary>
    public bool TryAddToChemicals(EntityUid uid, Solution solution, BloodstreamComponent? component = null)
    {
        return Resolve(uid, ref component, logMissing: false)
            && _solutionContainerSystem.ResolveSolution(uid, component.ChemicalSolutionName, ref component.ChemicalSolution)
            && _solutionContainerSystem.TryAddSolution(component.ChemicalSolution.Value, solution);
    }

    public bool FlushChemicals(EntityUid uid, string excludedReagentID, FixedPoint2 quantity, BloodstreamComponent? component = null)
    {
        if (!Resolve(uid, ref component, logMissing: false)
            || !_solutionContainerSystem.ResolveSolution(uid, component.ChemicalSolutionName, ref component.ChemicalSolution, out var chemSolution))
            return false;

        for (var i = chemSolution.Contents.Count - 1; i >= 0; i--)
        {
            var (reagentId, _) = chemSolution.Contents[i];
            if (reagentId.Prototype != excludedReagentID)
            {
                _solutionContainerSystem.RemoveReagent(component.ChemicalSolution.Value, reagentId, quantity);
            }
        }

        return true;
    }

    public float GetBloodLevelPercentage(EntityUid uid, BloodstreamComponent? component = null)
    {
        if (!Resolve(uid, ref component)
            || !_solutionContainerSystem.ResolveSolution(uid, component.BloodSolutionName, ref component.BloodSolution, out var bloodSolution))
        {
            return 0.0f;
        }

        return bloodSolution.FillFraction;
    }

    public void SetBloodLossThreshold(EntityUid uid, float threshold, BloodstreamComponent? comp = null)
    {
        if (!Resolve(uid, ref comp))
            return;

        comp.BloodlossThreshold = threshold;
    }

    /// <summary>
    ///     Attempts to modify the blood level of this entity directly.
    /// </summary>
    public bool TryModifyBloodLevel(EntityUid uid, FixedPoint2 amount, BloodstreamComponent? component = null)
    {
        if (!Resolve(uid, ref component, logMissing: false)
            || !_solutionContainerSystem.ResolveSolution(uid, component.BloodSolutionName, ref component.BloodSolution))
        {
            return false;
        }

        if (amount >= 0)
            return _solutionContainerSystem.TryAddReagent(component.BloodSolution.Value, component.BloodReagent, amount, null, GetEntityBloodData(uid));

        // Removal is more involved,
        // since we also wanna handle moving it to the temporary solution
        // and then spilling it if necessary.
        var newSol = _solutionContainerSystem.SplitSolution(component.BloodSolution.Value, -amount);

        if (!_solutionContainerSystem.ResolveSolution(uid, component.BloodTemporarySolutionName, ref component.TemporarySolution, out var tempSolution))
            return true;

        tempSolution.AddSolution(newSol, _prototypeManager);

        if (tempSolution.Volume > component.BleedPuddleThreshold)
        {
            // Pass some of the chemstream into the spilled blood.
            if (_solutionContainerSystem.ResolveSolution(uid, component.ChemicalSolutionName, ref component.ChemicalSolution))
            {
                var temp = _solutionContainerSystem.SplitSolution(component.ChemicalSolution.Value, tempSolution.Volume / 10);
                tempSolution.AddSolution(temp, _prototypeManager);
            }

            _puddleSystem.TrySpillAt(uid, tempSolution, out var puddleUid, sound: false);

            tempSolution.RemoveAllSolution();
        }

        _solutionContainerSystem.UpdateChemicals(component.TemporarySolution.Value);

        return true;
    }

    /// <summary>
    ///     Tries to make an entity bleed more or less
    /// </summary>
    public bool TryModifyBleedAmount(EntityUid uid, float amount, BloodstreamComponent? component = null)
    {
        if (!Resolve(uid, ref component, logMissing: false))
            return false;

        component.BleedAmount += amount;
        component.BleedAmount = Math.Clamp(component.BleedAmount, 0, component.MaxBleedAmount);

        if (component.BleedAmount == 0)
            _alertsSystem.ClearAlert(uid, component.BleedingAlert);
        else
        {
            var severity = (short) Math.Clamp(Math.Round(component.BleedAmount, MidpointRounding.ToZero), 0, 10);
            _alertsSystem.ShowAlert(uid, component.BleedingAlert, severity);
        }

        return true;
    }

    /// <summary>
    ///     BLOOD FOR THE BLOOD GOD
    /// </summary>
    public void SpillAllSolutions(EntityUid uid, BloodstreamComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        var tempSol = new Solution();

        if (_solutionContainerSystem.ResolveSolution(uid, component.BloodSolutionName, ref component.BloodSolution, out var bloodSolution))
        {
            tempSol.MaxVolume += bloodSolution.MaxVolume;
            tempSol.AddSolution(bloodSolution, _prototypeManager);
            _solutionContainerSystem.RemoveAllSolution(component.BloodSolution.Value);
        }

        if (_solutionContainerSystem.ResolveSolution(uid, component.ChemicalSolutionName, ref component.ChemicalSolution, out var chemSolution))
        {
            tempSol.MaxVolume += chemSolution.MaxVolume;
            tempSol.AddSolution(chemSolution, _prototypeManager);
            _solutionContainerSystem.RemoveAllSolution(component.ChemicalSolution.Value);
        }

        if (_solutionContainerSystem.ResolveSolution(uid, component.BloodTemporarySolutionName, ref component.TemporarySolution, out var tempSolution))
        {
            tempSol.MaxVolume += tempSolution.MaxVolume;
            tempSol.AddSolution(tempSolution, _prototypeManager);
            _solutionContainerSystem.RemoveAllSolution(component.TemporarySolution.Value);
        }

        _puddleSystem.TrySpillAt(uid, tempSol, out var puddleUid);
    }

    /// <summary>
    ///     Change what someone's blood is made of, on the fly.
    /// </summary>
    public void ChangeBloodReagent(EntityUid uid, string reagent, BloodstreamComponent? component = null)
    {
        if (!Resolve(uid, ref component, logMissing: false)
            || reagent == component.BloodReagent)
        {
            return;
        }

        if (!_solutionContainerSystem.ResolveSolution(uid, component.BloodSolutionName, ref component.BloodSolution, out var bloodSolution))
        {
            component.BloodReagent = reagent;
            return;
        }

        var currentVolume = bloodSolution.RemoveReagent(component.BloodReagent, bloodSolution.Volume, ignoreReagentData: true);

        component.BloodReagent = reagent;

        if (currentVolume > 0)
            _solutionContainerSystem.TryAddReagent(component.BloodSolution.Value, component.BloodReagent, currentVolume, null, GetEntityBloodData(uid));
    }

    private void OnDnaGenerated(Entity<BloodstreamComponent> entity, ref GenerateDnaEvent args)
    {
        if (_solutionContainerSystem.ResolveSolution(entity.Owner, entity.Comp.BloodSolutionName, ref entity.Comp.BloodSolution, out var bloodSolution))
        {
            foreach (var reagent in bloodSolution.Contents)
            {
                List<ReagentData> reagentData = reagent.Reagent.EnsureReagentData();
                reagentData.RemoveAll(x => x is DnaData);
                reagentData.AddRange(GetEntityBloodData(entity.Owner));
            }
        }
        else
            Log.Error("Unable to set bloodstream DNA, solution entity could not be resolved");
    }

    /// <summary>
    /// Get the reagent data for blood that a specific entity should have.
    /// </summary>
    public List<ReagentData> GetEntityBloodData(EntityUid uid)
    {
        var bloodData = new List<ReagentData>();
        var dnaData = new DnaData();

        if (TryComp<DnaComponent>(uid, out var donorComp) && donorComp.DNA != null)
        {
            dnaData.DNA = donorComp.DNA;
        }
        else
        {
            dnaData.DNA = Loc.GetString("forensics-dna-unknown");
        }

        bloodData.Add(dnaData);

        return bloodData;
    }

    // backmen edit start
    /// <summary>
    /// Self-explanatory
    /// </summary>
    /// <param name="uid">Wound entity</param>
    /// <param name="comp">Bleeds Inflicter Component </param>
    /// <returns>Returns whether if the wound can bleed</returns>
    public bool CanWoundBleed(EntityUid uid, BleedInflicterComponent? comp = null)
    {
        if (!_bleedsQuery.Resolve(uid, ref comp))
            return false;

        if (comp.BleedingModifiers.Count == 0)
            return true; // No modifiers. return true

        var lastCanBleed = true;
        var lastPriority = 0;
        foreach (var (_, pair) in comp.BleedingModifiers)
        {
            if (pair.Priority <= lastPriority)
                continue;

            lastPriority = pair.Priority;
            lastCanBleed = pair.CanBleed;
        }

        return lastCanBleed;
    }

    /// <summary>
    /// Add a bleed-ability modifier on woundable
    /// </summary>
    /// <param name="woundable">Entity uid of the woundable to apply the modifiers</param>
    /// <param name="identifier">string identifier of the modifier</param>
    /// <param name="priority">Priority of the said modifier</param>
    /// <param name="canBleed">Should the wounds bleed?</param>
    /// <param name="force">If forced, won't stop after failing to apply one modifier</param>
    /// <param name="woundableComp">Woundable Component</param>
    /// <returns>Return true if applied</returns>
    [PublicAPI]
    public bool TryAddBleedModifier(
        EntityUid woundable,
        string identifier,
        int priority,
        bool canBleed,
        bool force = false,
        WoundableComponent? woundableComp = null)
    {
        if (!_woundableQuery.Resolve(woundable, ref woundableComp))
            return false;

        foreach (var woundEnt in _wound.GetWoundableWoundsWithComp<BleedInflicterComponent>(woundable, woundableComp))
        {
            if (TryAddBleedModifier(woundEnt, identifier, priority, canBleed, woundEnt.Comp2))
                continue;

            if (!force)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Add a bleed-ability modifier
    /// </summary>
    /// <param name="uid">Entity uid of the wound</param>
    /// <param name="identifier">string identifier of the modifier</param>
    /// <param name="priority">Priority of the said modifier</param>
    /// <param name="canBleed">Should the wound bleed?</param>
    /// <param name="comp">Bleed Inflicter Component</param>
    /// <returns>Return true if applied</returns>
    [PublicAPI]
    public bool TryAddBleedModifier(
        EntityUid uid,
        string identifier,
        int priority,
        bool canBleed,
        BleedInflicterComponent? comp = null)
    {
        if (!_bleedsQuery.Resolve(uid, ref comp))
            return false;

        if (!comp.BleedingModifiers.TryAdd(identifier, (priority, canBleed)))
            return false;

        Dirty(uid, comp);
        return true;
    }

    /// <summary>
    /// Remove a bleed-ability modifier from a woundable
    /// </summary>
    /// <param name="uid">Entity uid of the woundable</param>
    /// <param name="identifier">string identifier of the modifier</param>
    /// <param name="force">If forced, won't stop applying modifiers after failing one wound</param>
    /// <param name="woundable">Woundable Component</param>
    /// <returns>Returns true if removed all modifiers ON WOUNDABLE</returns>
    [PublicAPI]
    public bool TryRemoveBleedModifier(
        EntityUid uid,
        string identifier,
        bool force = false,
        WoundableComponent? woundable = null)
    {
        if (!_woundableQuery.Resolve(uid, ref woundable))
            return false;

        foreach (var woundEnt in _wound.GetWoundableWoundsWithComp<BleedInflicterComponent>(uid, woundable))
        {
            if (TryRemoveBleedModifier(woundEnt, identifier, woundEnt.Comp2))
                continue;

            if (!force)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Remove a bleed-ability modifier
    /// </summary>
    /// <param name="uid">Entity uid of the wound</param>
    /// <param name="identifier">string identifier of the modifier</param>
    /// <param name="comp">Bleed Inflicter Component</param>
    /// <returns>Return true if removed</returns>
    public bool TryRemoveBleedModifier(
        EntityUid uid,
        string identifier,
        BleedInflicterComponent? comp = null)
    {
        if (!_bleedsQuery.Resolve(uid, ref comp))
            return false;

        if (!comp.BleedingModifiers.Remove(identifier))
            return false;

        Dirty(uid, comp);
        return true;
    }

    /// <summary>
    /// Redact a modifiers meta data
    /// </summary>
    /// <param name="wound">The wound entity uid</param>
    /// <param name="identifier">Identifier of the modifier</param>
    /// <param name="priority">Priority to set</param>
    /// <param name="canBleed">Should it bleed?</param>
    /// <param name="bleeds">Bleed Inflicter Component</param>
    /// <returns>true if was changed</returns>
    [PublicAPI]
    public bool ChangeBleedsModifierMetadata(
        EntityUid wound,
        string identifier,
        bool canBleed,
        int? priority,
        BleedInflicterComponent? bleeds = null)
    {
        if (!_bleedsQuery.Resolve(wound, ref bleeds))
            return false;

        if (!bleeds.BleedingModifiers.TryGetValue(identifier, out var pair))
            return false;

        bleeds.BleedingModifiers[identifier] = (Priority: priority ?? pair.Priority, CanBleed: canBleed);
        return true;
    }

    /// <summary>
    /// Redact a modifiers meta data
    /// </summary>
    /// <param name="wound">The wound entity uid</param>
    /// <param name="identifier">Identifier of the modifier</param>
    /// <param name="priority">Priority to set</param>
    /// <param name="canBleed">Should it bleed?</param>
    /// <param name="bleeds">Bleed Inflicter Component</param>
    /// <returns>true if was changed</returns>
    [PublicAPI]
    public bool ChangeBleedsModifierMetadata(
        EntityUid wound,
        string identifier,
        int priority,
        bool? canBleed,
        BleedInflicterComponent? bleeds = null)
    {
        if (!_bleedsQuery.Resolve(wound, ref bleeds))
            return false;

        if (!bleeds.BleedingModifiers.TryGetValue(identifier, out var pair))
            return false;

        bleeds.BleedingModifiers[identifier] = (Priority: priority, CanBleed: canBleed ?? pair.CanBleed);
        return true;
    }
    // backmen edit end
}
