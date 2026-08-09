// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Hands.Systems;
using Content.Server.Weapons.Ranged.Systems;
using Content.Server.Wieldable;
using Content.Shared.ActionBlocker;
using Content.Shared.Actions;
using Content.Shared.Actions.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.DeadSpace.Renegade;
using Content.Shared.DeadSpace.Renegade.Components;
using Content.Shared.DoAfter;
using Content.Shared.Ensnaring;
using Content.Shared.Ensnaring.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.Components;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Maps;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.StatusEffectNew;
using Content.Shared.StatusEffectNew.Components;
using Content.Shared.Stunnable;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Reflect;
using Content.Shared.Wieldable.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests.DeadSpace.Renegade;

[TestFixture]
public sealed class RenegadeCombatTests
{
    private const string TestRenegadeMob = "TestRenegadeCombatMob";
    private const string TestRenegadeSword = "TestRenegadeCombatSword";
    private const string TestRageAction = "TestRenegadeFocusedRageAction";
    private const string TestChokeAction = "TestRenegadeForceChokeAction";
    private const string TestEnsnare = "TestRenegadeEnsnare";
    private const string TestSpreadAmmo = "TestRenegadeSpreadAmmo";

    private const float ArmorHeatDamage = 7f;
    private const float ArmorSlashDamage = 11f;
    private const float TechniqueCooldown = 0.2f;
    private const float KnockdownDuration = 0.12f;
    private const float RageDuration = 0.2f;
    private const float RageSpeedModifier = 1.37f;
    private const float RageEnsnareMultiplier = 0.25f;
    private const float RageStunMultiplier = 0.3f;
    private const float RageActionCooldown = 0.45f;
    private const float ChokeTickInterval = 0.04f;
    private const int ChokeTickCount = 3;
    private const float ChokeDamage = 2f;
    private const float ChokeSpreadMultiplier = 3f;
    private const float ChokeActionCooldown = 0.35f;
    private const float TestAmmoSpread = 8f;

    [TestPrototypes]
    private static readonly string TestPrototypes = FormattableString.Invariant($$"""
        - type: entity
          id: {{TestRenegadeMob}}
          parent: MobHuman
          components:
          - type: Renegade
            focusedRageAction: {{TestRageAction}}
            forceChokeAction: {{TestChokeAction}}
            armorPiercingDamage:
              types:
                Heat: {{ArmorHeatDamage}}
                Slash: {{ArmorSlashDamage}}
            armorPiercingStrikeCooldown: {{TechniqueCooldown}}
            disarmStrikeCooldown: {{TechniqueCooldown}}
            knockdownStrikeCooldown: {{TechniqueCooldown}}
            knockdownStrikeDuration: {{KnockdownDuration}}
            focusedRageDuration: {{RageDuration}}
            focusedRageSpeedModifier: {{RageSpeedModifier}}
            focusedRageEnsnareDurationMultiplier: {{RageEnsnareMultiplier}}
            focusedRageStunDurationMultiplier: {{RageStunMultiplier}}
            forceChokeRange: 2
            forceChokeDuration: {{ChokeTickInterval * ChokeTickCount}}
            forceChokeTickInterval: {{ChokeTickInterval}}
            forceChokeTickCount: {{ChokeTickCount}}
            forceChokeDamage:
              types:
                Poison: {{ChokeDamage}}
            forceChokeSpreadMultiplier: {{ChokeSpreadMultiplier}}

        - type: entity
          id: {{TestRenegadeSword}}
          parent: EnergySwordRenegade
          components:
          - type: Wieldable
            useDelayOnWield: false
          - type: Reflect
            reflectProb: 1

        - type: entity
          id: {{TestRageAction}}
          categories: [ HideSpawnMenu ]
          components:
          - type: Action
            useDelay: {{RageActionCooldown}}
          - type: InstantAction
            event: !type:RenegadeFocusedRageEvent

        - type: entity
          id: {{TestChokeAction}}
          categories: [ HideSpawnMenu ]
          components:
          - type: Action
            useDelay: {{ChokeActionCooldown}}
          - type: TargetAction
            range: 2
          - type: EntityTargetAction
            canTargetSelf: false
            event: !type:RenegadeForceChokeEvent

        - type: entity
          id: {{TestEnsnare}}
          parent: BaseItem
          components:
          - type: Ensnaring
            breakoutTime: 0.8
            staminaDamage: 0
            canMoveBreakout: true

        - type: entity
          id: {{TestSpreadAmmo}}
          parent: PelletGlassSpread
          components:
          - type: Ammo
          - type: ProjectileSpread
            proto: PelletGlass
            count: 3
            spread: {{TestAmmoSpread}}
        """);

    [Test]
    public async Task SwordRequiresRenegadeAndTwoHandsAndKeepsActionsAfterDrop()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = false,
            Dirty = true,
        });

        var server = pair.Server;
        var entMan = server.EntMan;
        var hands = server.System<HandsSystem>();
        var itemToggle = server.System<ItemToggleSystem>();
        var wield = server.System<WieldableSystem>();

        await server.WaitAssertion(() =>
        {
            var mapId = CreateMap(server);
            var coordinates = new MapCoordinates(Vector2.Zero, mapId);
            var renegade = entMan.SpawnEntity(TestRenegadeMob, coordinates);
            var outsider = entMan.SpawnEntity("MobHuman", coordinates);
            var sword = entMan.SpawnEntity(TestRenegadeSword, coordinates);
            var toggle = entMan.GetComponent<ItemToggleComponent>(sword);
            var wieldable = entMan.GetComponent<WieldableComponent>(sword);
            var renegadeComp = entMan.GetComponent<RenegadeComponent>(renegade);

            Assert.That(itemToggle.TryActivate(sword, outsider, predicted: false), Is.False);
            Assert.That(toggle.Activated, Is.False);
            Assert.That(hands.TryPickupAnyHand(renegade, sword, checkActionBlocker: false), Is.True);

            var inactiveReflection = new HitScanReflectAttemptEvent(
                outsider,
                outsider,
                ReflectType.Energy,
                Vector2.UnitX,
                false);
            entMan.EventBus.RaiseLocalEvent(renegade, ref inactiveReflection);
            Assert.That(inactiveReflection.Reflected, Is.False);

            Assert.That(wield.TryWield(sword, wieldable, renegade), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(toggle.Activated, Is.True);
                Assert.That(wieldable.Wielded, Is.True);
                Assert.That(hands.CountFreeHands((renegade, entMan.GetComponent<HandsComponent>(renegade))), Is.Zero);
            });

            var activeReflection = new HitScanReflectAttemptEvent(
                outsider,
                outsider,
                ReflectType.Energy,
                Vector2.UnitX,
                false);
            entMan.EventBus.RaiseLocalEvent(renegade, ref activeReflection);
            Assert.That(activeReflection.Reflected, Is.True);

            var persistentActions = new EntityUid?[]
            {
                renegadeComp.ArmorPiercingStrikeActionEntity,
                renegadeComp.DisarmStrikeActionEntity,
                renegadeComp.KnockdownStrikeActionEntity,
                renegadeComp.FocusedRageActionEntity,
                renegadeComp.ForceChokeActionEntity,
            };
            Assert.That(persistentActions, Has.All.Not.Null);

            Assert.That(hands.TryDrop(renegade, sword, checkActionBlocker: false), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(toggle.Activated, Is.False);
                Assert.That(wieldable.Wielded, Is.False);
                Assert.That(persistentActions, Has.All.Matches<EntityUid?>(uid =>
                    uid is { } action && entMan.EntityExists(action)));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SwordTechniquesPrimeUntilAHitAndApplyFunctionalEffects()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = false,
            Dirty = true,
        });

        var server = pair.Server;
        var entMan = server.EntMan;
        var actions = server.System<SharedActionsSystem>();
        var hands = server.System<HandsSystem>();
        var stun = server.System<SharedStunSystem>();
        var timing = server.ResolveDependency<IGameTiming>();
        var wield = server.System<WieldableSystem>();
        EntityUid target = default;
        EntityUid retainedItem = default;

        await server.WaitAssertion(() =>
        {
            var mapId = CreateMap(server);
            var coordinates = new MapCoordinates(Vector2.Zero, mapId);
            var renegade = entMan.SpawnEntity(TestRenegadeMob, coordinates);
            target = entMan.SpawnEntity("MobHuman", coordinates);
            var sword = entMan.SpawnEntity(TestRenegadeSword, coordinates);
            var renegadeComp = entMan.GetComponent<RenegadeComponent>(renegade);

            Assert.That(hands.TryPickupAnyHand(renegade, sword, checkActionBlocker: false), Is.True);
            Assert.That(wield.TryWield(sword, entMan.GetComponent<WieldableComponent>(sword), renegade), Is.True);

            var armorActionUid = renegadeComp.ArmorPiercingStrikeActionEntity!.Value;
            var armorAction = entMan.GetComponent<ActionComponent>(armorActionUid);
            actions.PerformAction(renegade, (armorActionUid, armorAction));
            Assert.That(renegadeComp.SelectedTechnique, Is.EqualTo(RenegadeSwordTechnique.ArmorPiercing));
            Assert.That(armorAction.Cooldown, Is.Null);

            var miss = new MeleeHitEvent([], renegade, sword, new DamageSpecifier(), null);
            entMan.EventBus.RaiseLocalEvent(sword, miss);
            Assert.Multiple(() =>
            {
                Assert.That(renegadeComp.SelectedTechnique, Is.EqualTo(RenegadeSwordTechnique.ArmorPiercing));
                Assert.That(armorAction.Cooldown, Is.Null);
            });

            var baseDamage = new DamageSpecifier();
            baseDamage.DamageDict["Blunt"] = FixedPoint2.New(50);
            var damage = new GetMeleeDamageEvent(sword, baseDamage, [], renegade);
            entMan.EventBus.RaiseLocalEvent(sword, ref damage);
            Assert.Multiple(() =>
            {
                Assert.That(damage.ResistanceBypass, Is.True);
                Assert.That(damage.Damage.DamageDict.ContainsKey("Blunt"), Is.False);
                Assert.That(damage.Damage.DamageDict["Heat"], Is.EqualTo(FixedPoint2.New(ArmorHeatDamage)));
                Assert.That(damage.Damage.DamageDict["Slash"], Is.EqualTo(FixedPoint2.New(ArmorSlashDamage)));
            });

            var hit = new MeleeHitEvent([target], renegade, sword, damage.Damage, null);
            entMan.EventBus.RaiseLocalEvent(sword, hit);
            Assert.That(renegadeComp.SelectedTechnique, Is.Null);
            AssertCooldown(armorAction, timing.CurTime, TechniqueCooldown);

            var targetItem = entMan.SpawnEntity("Crowbar", coordinates);
            Assert.That(hands.TryPickupAnyHand(target, targetItem, checkActionBlocker: false), Is.True);
            var disarmActionUid = renegadeComp.DisarmStrikeActionEntity!.Value;
            var disarmAction = entMan.GetComponent<ActionComponent>(disarmActionUid);
            actions.PerformAction(renegade, (disarmActionUid, disarmAction));
            entMan.EventBus.RaiseLocalEvent(sword,
                new MeleeHitEvent([target], renegade, sword, new DamageSpecifier(), null));
            Assert.Multiple(() =>
            {
                Assert.That(hands.IsHolding((target, entMan.GetComponent<HandsComponent>(target)), targetItem), Is.False);
                Assert.That(entMan.HasComponent<StunnedComponent>(target), Is.False);
            });
            AssertCooldown(disarmAction, timing.CurTime, TechniqueCooldown);

            retainedItem = entMan.SpawnEntity("Screwdriver", coordinates);
            Assert.That(hands.TryPickupAnyHand(target, retainedItem, checkActionBlocker: false), Is.True);
            var knockdownActionUid = renegadeComp.KnockdownStrikeActionEntity!.Value;
            var knockdownAction = entMan.GetComponent<ActionComponent>(knockdownActionUid);
            var knockdownStarted = timing.CurTime;
            actions.PerformAction(renegade, (knockdownActionUid, knockdownAction));
            entMan.EventBus.RaiseLocalEvent(sword,
                new MeleeHitEvent([target], renegade, sword, new DamageSpecifier(), null));

            var knockedDown = entMan.GetComponent<KnockedDownComponent>(target);
            Assert.Multiple(() =>
            {
                Assert.That(entMan.HasComponent<StunnedComponent>(target), Is.False);
                Assert.That(hands.IsHolding((target, entMan.GetComponent<HandsComponent>(target)), retainedItem), Is.True);
                Assert.That(knockedDown.AutoStand, Is.True);
                Assert.That(stun.TryStanding((target, knockedDown)), Is.False);
                Assert.That(knockedDown.NextUpdate - knockdownStarted,
                    Is.EqualTo(TimeSpan.FromSeconds(KnockdownDuration)).Within(TimeSpan.FromSeconds(0.03)));
            });
            AssertCooldown(knockdownAction, timing.CurTime, TechniqueCooldown);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FocusedRageUsesInjectedModifiersAndPreservesExistingImmunity()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = false,
            Dirty = true,
        });

        var server = pair.Server;
        var entMan = server.EntMan;
        var actions = server.System<SharedActionsSystem>();
        var ensnare = server.System<SharedEnsnareableSystem>();
        var statusEffects = server.System<StatusEffectsSystem>();
        var stun = server.System<SharedStunSystem>();
        var timing = server.ResolveDependency<IGameTiming>();
        EntityUid renegade = default;
        EntityUid preImmuneRenegade = default;

        await server.WaitAssertion(() =>
        {
            var mapId = CreateMap(server);
            var coordinates = new MapCoordinates(Vector2.Zero, mapId);
            renegade = entMan.SpawnEntity(TestRenegadeMob, coordinates);
            preImmuneRenegade = entMan.SpawnEntity(TestRenegadeMob, coordinates);
            entMan.EnsureComponent<IgnoreSlowOnDamageComponent>(preImmuneRenegade);

            ActivateRage(entMan, actions, renegade);
            ActivateRage(entMan, actions, preImmuneRenegade);
            Assert.Multiple(() =>
            {
                Assert.That(entMan.HasComponent<RenegadeFocusedRageComponent>(renegade), Is.True);
                Assert.That(entMan.HasComponent<IgnoreSlowOnDamageComponent>(renegade), Is.True);
                Assert.That(entMan.GetComponent<MovementSpeedModifierComponent>(renegade).WalkSpeedModifier,
                    Is.EqualTo(RageSpeedModifier).Within(0.001f));
            });

            var rageAction = entMan.GetComponent<ActionComponent>(
                entMan.GetComponent<RenegadeComponent>(renegade).FocusedRageActionEntity!.Value);
            AssertCooldown(rageAction, timing.CurTime, RageActionCooldown);

            var ensnaringItem = entMan.SpawnEntity(TestEnsnare, coordinates);
            var ensnaringComp = entMan.GetComponent<EnsnaringComponent>(ensnaringItem);
            Assert.That(ensnare.TryEnsnare(renegade, ensnaringItem, ensnaringComp), Is.True);
            ensnare.TryFree(renegade, renegade, ensnaringItem, ensnaringComp);
            var doAfter = entMan.GetComponent<DoAfterComponent>(renegade).DoAfters.Values.Single();
            Assert.That(doAfter.Args.Delay,
                Is.EqualTo(TimeSpan.FromSeconds(0.8f * RageEnsnareMultiplier)).Within(TimeSpan.FromSeconds(0.01)));
            ensnare.ForceFree(ensnaringItem, ensnaringComp);

            var stunStarted = timing.CurTime;
            Assert.That(stun.TryAddStunDuration(renegade, TimeSpan.FromSeconds(1)), Is.True);
            Assert.That(statusEffects.TryGetStatusEffect(renegade, SharedStunSystem.StunId, out var statusEffect), Is.True);
            var status = entMan.GetComponent<StatusEffectComponent>(statusEffect!.Value);
            Assert.That(status.EndEffectTime!.Value - stunStarted,
                Is.EqualTo(TimeSpan.FromSeconds(RageStunMultiplier)).Within(TimeSpan.FromSeconds(0.03)));
        });

        await server.WaitRunTicks(20);
        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entMan.HasComponent<RenegadeFocusedRageComponent>(renegade), Is.False);
                Assert.That(entMan.HasComponent<IgnoreSlowOnDamageComponent>(renegade), Is.False);
                Assert.That(entMan.GetComponent<MovementSpeedModifierComponent>(renegade).WalkSpeedModifier,
                    Is.EqualTo(1f).Within(0.001f));
                Assert.That(entMan.HasComponent<RenegadeFocusedRageComponent>(preImmuneRenegade), Is.False);
                Assert.That(entMan.HasComponent<IgnoreSlowOnDamageComponent>(preImmuneRenegade), Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ForceChokeStopsOnlyMovementTicksDamageAndMultipliesWeaponSpread()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = false,
            Dirty = true,
        });

        var server = pair.Server;
        var entMan = server.EntMan;
        var actionBlocker = server.System<ActionBlockerSystem>();
        var actions = server.System<SharedActionsSystem>();
        var timing = server.ResolveDependency<IGameTiming>();
        EntityUid target = default;
        FixedPoint2 startingPoison = default;

        await server.WaitAssertion(() =>
        {
            var mapId = CreateMap(server);
            var renegadeCoordinates = new MapCoordinates(Vector2.Zero, mapId);
            var targetCoordinates = new MapCoordinates(Vector2.UnitX, mapId);
            var renegade = entMan.SpawnEntity(TestRenegadeMob, renegadeCoordinates);
            target = entMan.SpawnEntity("MobHuman", targetCoordinates);
            var normalShooter = entMan.SpawnEntity("MobHuman", new MapCoordinates(new Vector2(0f, 2f), mapId));
            startingPoison = GetDamage(entMan, target, "Poison");

            var renegadeComp = entMan.GetComponent<RenegadeComponent>(renegade);
            var chokeActionUid = renegadeComp.ForceChokeActionEntity!.Value;
            var chokeAction = entMan.GetComponent<ActionComponent>(chokeActionUid);
            actions.PerformAction(renegade,
                (chokeActionUid, chokeAction),
                new RenegadeForceChokeEvent { Target = target });

            Assert.That(entMan.HasComponent<RenegadeForceChokeComponent>(target), Is.True);
            AssertCooldown(chokeAction, timing.CurTime, ChokeActionCooldown);

            var movement = entMan.GetComponent<MovementSpeedModifierComponent>(target);
            Assert.Multiple(() =>
            {
                Assert.That(movement.WalkSpeedModifier, Is.Zero);
                Assert.That(movement.SprintSpeedModifier, Is.Zero);
                Assert.That(entMan.HasComponent<StunnedComponent>(target), Is.False);
                Assert.That(entMan.HasComponent<KnockedDownComponent>(target), Is.False);
                Assert.That(actionBlocker.CanInteract(target, null), Is.True);
                Assert.That(actionBlocker.CanAttack(target), Is.True);
            });

            var normalAngle = FireSpreadShot(server, normalShooter, mapId, new Vector2(0f, 2f), out var normalImpulse);
            var chokedAngle = FireSpreadShot(server, target, mapId, Vector2.UnitX, out var chokedImpulse);
            Assert.Multiple(() =>
            {
                Assert.That(normalImpulse, Is.True);
                Assert.That(chokedImpulse, Is.True);
                Assert.That(chokedAngle,
                    Is.EqualTo(normalAngle * ChokeSpreadMultiplier).Within(0.002));
            });
        });

        await server.WaitRunTicks(12);
        await server.WaitAssertion(() =>
        {
            var speedRefresh = new RefreshMovementSpeedModifiersEvent();
            entMan.EventBus.RaiseLocalEvent(target, speedRefresh);
            Assert.Multiple(() =>
            {
                Assert.That(entMan.HasComponent<RenegadeForceChokeComponent>(target), Is.False);
                Assert.That(speedRefresh.WalkSpeedModifier, Is.GreaterThan(0f));
                Assert.That(speedRefresh.SprintSpeedModifier, Is.GreaterThan(0f));
                Assert.That(GetDamage(entMan, target, "Poison") - startingPoison,
                    Is.EqualTo(FixedPoint2.New(ChokeDamage * ChokeTickCount)));
            });
        });

        await pair.CleanReturnAsync();
    }

    private static MapId CreateMap(RobustIntegrationTest.ServerIntegrationInstance server)
    {
        server.System<SharedMapSystem>().CreateMap(out var mapId);
        return mapId;
    }

    private static void ActivateRage(IEntityManager entMan, SharedActionsSystem actions, EntityUid user)
    {
        var renegade = entMan.GetComponent<RenegadeComponent>(user);
        var actionUid = renegade.FocusedRageActionEntity!.Value;
        actions.PerformAction(user, (actionUid, entMan.GetComponent<ActionComponent>(actionUid)));
    }

    private static void AssertCooldown(ActionComponent action, TimeSpan now, float expectedSeconds)
    {
        Assert.That(action.Cooldown, Is.Not.Null);
        var cooldown = action.Cooldown!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(cooldown.Start, Is.LessThanOrEqualTo(now));
            Assert.That(cooldown.End - cooldown.Start,
                Is.EqualTo(TimeSpan.FromSeconds(expectedSeconds)).Within(TimeSpan.FromSeconds(0.01)));
        });
    }

    private static FixedPoint2 GetDamage(IEntityManager entMan, EntityUid uid, string damageType)
    {
        var damage = entMan.GetComponent<DamageableComponent>(uid).Damage;
        return damage.DamageDict.GetValueOrDefault(damageType, FixedPoint2.Zero);
    }

    private static double FireSpreadShot(
        RobustIntegrationTest.ServerIntegrationInstance server,
        EntityUid shooter,
        MapId mapId,
        Vector2 position,
        out bool userImpulse)
    {
        var entMan = server.EntMan;
        var gunSystem = server.System<GunSystem>();
        var coordinates = new MapCoordinates(position, mapId);
        var gun = entMan.SpawnEntity("WeaponPistolMk58", coordinates);
        var ammo = entMan.SpawnEntity(TestSpreadAmmo, coordinates);
        var from = entMan.GetComponent<TransformComponent>(shooter).Coordinates;
        var to = new EntityCoordinates(from.EntityId, from.Position + Vector2.UnitX * 10f);
        var ammoComponent = entMan.GetComponent<AmmoComponent>(ammo);

        gunSystem.Shoot(
            (gun, entMan.GetComponent<GunComponent>(gun)),
            [(ammo, ammoComponent)],
            from,
            to,
            out userImpulse,
            shooter);

        var velocity = entMan.GetComponent<PhysicsComponent>(ammo).LinearVelocity;
        return Math.Abs(Math.Atan2(velocity.Y, velocity.X));
    }
}
