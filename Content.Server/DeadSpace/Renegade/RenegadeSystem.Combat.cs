// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

using Content.Shared.Actions;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.DeadSpace.Renegade;
using Content.Shared.DeadSpace.Renegade.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Systems;
using Content.Shared.Popups;
using Content.Shared.Stunnable;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Wieldable.Components;
using Robust.Shared.Timing;

namespace Content.Server.DeadSpace.Renegade;

public sealed partial class RenegadeSystem
{
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movement = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private void InitializeCombat()
    {
        SubscribeLocalEvent<RenegadeComponent, RenegadeArmorPiercingStrikeEvent>(OnArmorPiercingStrike);
        SubscribeLocalEvent<RenegadeComponent, RenegadeDisarmStrikeEvent>(OnDisarmStrike);
        SubscribeLocalEvent<RenegadeComponent, RenegadeKnockdownStrikeEvent>(OnKnockdownStrike);
        SubscribeLocalEvent<RenegadeComponent, RenegadeFocusedRageEvent>(OnFocusedRage);
        SubscribeLocalEvent<RenegadeComponent, RenegadeForceChokeEvent>(OnForceChoke);

        SubscribeLocalEvent<RenegadeEswordComponent, GetMeleeDamageEvent>(OnGetMeleeDamage);
        SubscribeLocalEvent<RenegadeEswordComponent, MeleeHitEvent>(OnMeleeHit);

        SubscribeLocalEvent<RenegadeFocusedRageComponent, RefreshMovementSpeedModifiersEvent>(OnRageRefreshSpeed);
        SubscribeLocalEvent<RenegadeFocusedRageComponent, ModifyStunDurationEvent>(OnRageModifyStunDuration);
        SubscribeLocalEvent<RenegadeFocusedRageComponent, ComponentShutdown>(OnRageShutdown);

        SubscribeLocalEvent<RenegadeForceChokeComponent, RefreshMovementSpeedModifiersEvent>(OnChokeRefreshSpeed);
        SubscribeLocalEvent<RenegadeForceChokeComponent, SelfBeforeGunShotEvent>(OnChokeBeforeGunShot);
        SubscribeLocalEvent<RenegadeForceChokeComponent, ComponentShutdown>(OnChokeShutdown);
    }

    private void GrantCombatActions(Entity<RenegadeComponent> ent)
    {
        _actions.AddAction(ent.Owner, ref ent.Comp.ArmorPiercingStrikeActionEntity, ent.Comp.ArmorPiercingStrikeAction, ent.Owner);
        _actions.AddAction(ent.Owner, ref ent.Comp.DisarmStrikeActionEntity, ent.Comp.DisarmStrikeAction, ent.Owner);
        _actions.AddAction(ent.Owner, ref ent.Comp.KnockdownStrikeActionEntity, ent.Comp.KnockdownStrikeAction, ent.Owner);
        _actions.AddAction(ent.Owner, ref ent.Comp.FocusedRageActionEntity, ent.Comp.FocusedRageAction, ent.Owner);
        _actions.AddAction(ent.Owner, ref ent.Comp.ForceChokeActionEntity, ent.Comp.ForceChokeAction, ent.Owner);
    }

    private void RemoveCombatActions(Entity<RenegadeComponent> ent)
    {
        _actions.RemoveAction(ent.Owner, ent.Comp.ArmorPiercingStrikeActionEntity);
        _actions.RemoveAction(ent.Owner, ent.Comp.DisarmStrikeActionEntity);
        _actions.RemoveAction(ent.Owner, ent.Comp.KnockdownStrikeActionEntity);
        _actions.RemoveAction(ent.Owner, ent.Comp.FocusedRageActionEntity);
        _actions.RemoveAction(ent.Owner, ent.Comp.ForceChokeActionEntity);
    }

    private void OnArmorPiercingStrike(Entity<RenegadeComponent> ent, ref RenegadeArmorPiercingStrikeEvent args)
    {
        SelectTechnique(ent, RenegadeSwordTechnique.ArmorPiercing);
        args.Handled = true;
    }

    private void OnDisarmStrike(Entity<RenegadeComponent> ent, ref RenegadeDisarmStrikeEvent args)
    {
        SelectTechnique(ent, RenegadeSwordTechnique.Disarm);
        args.Handled = true;
    }

    private void OnKnockdownStrike(Entity<RenegadeComponent> ent, ref RenegadeKnockdownStrikeEvent args)
    {
        SelectTechnique(ent, RenegadeSwordTechnique.Knockdown);
        args.Handled = true;
    }

    private void SelectTechnique(Entity<RenegadeComponent> ent, RenegadeSwordTechnique technique)
    {
        if (ent.Comp.SelectedTechnique == technique)
        {
            ent.Comp.SelectedTechnique = null;
            SetTechniqueToggles(ent, null);
            _popup.PopupEntity(Loc.GetString("renegade-sword-technique-cancelled"), ent, ent);
            return;
        }

        ent.Comp.SelectedTechnique = technique;
        SetTechniqueToggles(ent, technique);
        _popup.PopupEntity(Loc.GetString("renegade-sword-technique-selected",
            ("technique", GetTechniqueName(technique))), ent, ent);
    }

    private string GetTechniqueName(RenegadeSwordTechnique technique)
    {
        return Loc.GetString(technique switch
        {
            RenegadeSwordTechnique.ArmorPiercing => "renegade-sword-technique-armor-piercing",
            RenegadeSwordTechnique.Disarm => "renegade-sword-technique-disarm",
            RenegadeSwordTechnique.Knockdown => "renegade-sword-technique-knockdown",
            _ => throw new ArgumentOutOfRangeException(nameof(technique), technique, null),
        });
    }

    private void SetTechniqueToggles(Entity<RenegadeComponent> ent, RenegadeSwordTechnique? technique)
    {
        _actions.SetToggled(ent.Comp.ArmorPiercingStrikeActionEntity,
            technique == RenegadeSwordTechnique.ArmorPiercing);
        _actions.SetToggled(ent.Comp.DisarmStrikeActionEntity,
            technique == RenegadeSwordTechnique.Disarm);
        _actions.SetToggled(ent.Comp.KnockdownStrikeActionEntity,
            technique == RenegadeSwordTechnique.Knockdown);
    }

    private void OnGetMeleeDamage(Entity<RenegadeEswordComponent> sword, ref GetMeleeDamageEvent args)
    {
        if (!TryGetTechniqueUser(args.User, sword, out var renegade) ||
            renegade.SelectedTechnique != RenegadeSwordTechnique.ArmorPiercing)
            return;

        args.Damage = new(renegade.ArmorPiercingDamage);
        args.Modifiers.Clear();
        args.ResistanceBypass = true;
    }

    private void OnMeleeHit(Entity<RenegadeEswordComponent> sword, ref MeleeHitEvent args)
    {
        if (!args.IsHit ||
            args.HitEntities.Count == 0 ||
            !TryGetTechniqueUser(args.User, sword, out var renegade) ||
            renegade.SelectedTechnique is not { } technique)
            return;

        EntityUid? mobTarget = null;
        foreach (var target in args.HitEntities)
        {
            if (!HasComp<MobStateComponent>(target))
                continue;

            mobTarget = target;
            break;
        }

        switch (technique)
        {
            case RenegadeSwordTechnique.Disarm when mobTarget is { } target:
                TryDisarmActiveHand(target);
                break;
            case RenegadeSwordTechnique.Knockdown when mobTarget is { } target:
                _stun.TryCrawling(target,
                    renegade.KnockdownStrikeDuration,
                    autoStand: true,
                    drop: false,
                    force: true);
                break;
        }

        ConsumeTechnique((args.User, renegade), technique);
    }

    private bool TryGetTechniqueUser(EntityUid user,
        Entity<RenegadeEswordComponent> sword,
        out RenegadeComponent renegade)
    {
        renegade = null!;
        if (!TryComp<RenegadeComponent>(user, out var component) ||
            !TryComp<ItemToggleComponent>(sword, out var toggle) ||
            !toggle.Activated ||
            !TryComp<WieldableComponent>(sword, out var wieldable) ||
            !wieldable.Wielded)
            return false;

        renegade = component;
        return true;
    }

    private void TryDisarmActiveHand(EntityUid target)
    {
        if (!_hands.TryGetActiveItem(target, out var heldItem))
            return;

        var item = heldItem.Value;
        if (TryComp<VirtualItemComponent>(item, out var virtualItem))
            item = virtualItem.BlockingEntity;

        _hands.TryDrop(target, item, checkActionBlocker: false);
    }

    private void ConsumeTechnique(Entity<RenegadeComponent> ent, RenegadeSwordTechnique technique)
    {
        ent.Comp.SelectedTechnique = null;
        SetTechniqueToggles(ent, null);

        var (action, cooldown) = technique switch
        {
            RenegadeSwordTechnique.ArmorPiercing =>
                (ent.Comp.ArmorPiercingStrikeActionEntity, ent.Comp.ArmorPiercingStrikeCooldown),
            RenegadeSwordTechnique.Disarm =>
                (ent.Comp.DisarmStrikeActionEntity, ent.Comp.DisarmStrikeCooldown),
            RenegadeSwordTechnique.Knockdown =>
                (ent.Comp.KnockdownStrikeActionEntity, ent.Comp.KnockdownStrikeCooldown),
            _ => throw new ArgumentOutOfRangeException(nameof(technique), technique, null),
        };

        _actions.SetCooldown(action, cooldown);
    }

    private void OnFocusedRage(Entity<RenegadeComponent> ent, ref RenegadeFocusedRageEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        var rage = EnsureComp<RenegadeFocusedRageComponent>(ent);
        rage.EndsAt = _timing.CurTime + ent.Comp.FocusedRageDuration;
        rage.SpeedModifier = ent.Comp.FocusedRageSpeedModifier;
        rage.EnsnareDurationMultiplier = ent.Comp.FocusedRageEnsnareDurationMultiplier;
        rage.StunDurationMultiplier = ent.Comp.FocusedRageStunDurationMultiplier;

        if (!HasComp<IgnoreSlowOnDamageComponent>(ent))
        {
            AddComp<IgnoreSlowOnDamageComponent>(ent);
            rage.AppliedIgnoreSlowOnDamage = true;
        }

        _movement.RefreshMovementSpeedModifiers(ent);
    }

    private void OnRageRefreshSpeed(Entity<RenegadeFocusedRageComponent> ent,
        ref RefreshMovementSpeedModifiersEvent args)
    {
        if (_timing.CurTime < ent.Comp.EndsAt)
            args.ModifySpeed(ent.Comp.SpeedModifier);
    }

    private void OnRageModifyStunDuration(Entity<RenegadeFocusedRageComponent> ent,
        ref ModifyStunDurationEvent args)
    {
        if (_timing.CurTime < ent.Comp.EndsAt)
            args.Duration *= ent.Comp.StunDurationMultiplier;
    }

    private void OnRageShutdown(Entity<RenegadeFocusedRageComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.AppliedIgnoreSlowOnDamage)
            RemComp<IgnoreSlowOnDamageComponent>(ent);

        if (!TerminatingOrDeleted(ent))
            _movement.RefreshMovementSpeedModifiers(ent);
    }

    private void OnForceChoke(Entity<RenegadeComponent> ent, ref RenegadeForceChokeEvent args)
    {
        var target = args.Target;
        if (args.Handled ||
            target == ent.Owner ||
            !TryComp<MobStateComponent>(target, out var mobState) ||
            !_mobState.IsAlive(target, mobState) ||
            !_transform.InRange(ent.Owner, target, ent.Comp.ForceChokeRange))
            return;

        args.Handled = true;
        var choke = EnsureComp<RenegadeForceChokeComponent>(target);
        choke.Source = ent;
        choke.EndsAt = _timing.CurTime + ent.Comp.ForceChokeDuration;
        choke.TickInterval = ent.Comp.ForceChokeTickInterval;
        choke.NextDamageTime = _timing.CurTime + choke.TickInterval;
        choke.RemainingTicks = ent.Comp.ForceChokeTickCount;
        choke.Damage = new(ent.Comp.ForceChokeDamage);
        choke.SpreadMultiplier = ent.Comp.ForceChokeSpreadMultiplier;

        _movement.RefreshMovementSpeedModifiers(target);
        _popup.PopupEntity(Loc.GetString("renegade-force-choke-lift", ("target", target)),
            target,
            PopupType.Large);
    }

    private void OnChokeRefreshSpeed(Entity<RenegadeForceChokeComponent> ent,
        ref RefreshMovementSpeedModifiersEvent args)
    {
        if (_timing.CurTime < ent.Comp.EndsAt)
            args.ModifySpeed(0f, 0f);
    }

    private void OnChokeBeforeGunShot(Entity<RenegadeForceChokeComponent> ent, ref SelfBeforeGunShotEvent args)
    {
        if (_timing.CurTime < ent.Comp.EndsAt)
            args.SpreadMultiplier *= ent.Comp.SpreadMultiplier;
    }

    private void OnChokeShutdown(Entity<RenegadeForceChokeComponent> ent, ref ComponentShutdown args)
    {
        if (!TerminatingOrDeleted(ent))
            _movement.RefreshMovementSpeedModifiers(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var currentTime = _timing.CurTime;

        var rageQuery = EntityQueryEnumerator<RenegadeFocusedRageComponent>();
        while (rageQuery.MoveNext(out var uid, out var rage))
        {
            if (currentTime >= rage.EndsAt)
                RemComp<RenegadeFocusedRageComponent>(uid);
        }

        var chokeQuery = EntityQueryEnumerator<RenegadeForceChokeComponent>();
        while (chokeQuery.MoveNext(out var uid, out var choke))
        {
            if (!_mobState.IsAlive(uid))
            {
                RemComp<RenegadeForceChokeComponent>(uid);
                continue;
            }

            while (choke.RemainingTicks > 0 && currentTime >= choke.NextDamageTime)
            {
                _damageable.TryChangeDamage(uid, choke.Damage, origin: choke.Source);
                choke.RemainingTicks--;
                choke.NextDamageTime += choke.TickInterval;
            }

            if (choke.RemainingTicks <= 0 || currentTime >= choke.EndsAt)
                RemComp<RenegadeForceChokeComponent>(uid);
        }
    }
}
