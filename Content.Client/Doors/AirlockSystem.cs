using System.Numerics; // DS14
using Content.Client.Wires.Visualizers;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Power;
using Robust.Client.Animations;
using Robust.Client.GameObjects;
using Robust.Shared.Maths; // DS14

namespace Content.Client.Doors;

public sealed class AirlockSystem : SharedAirlockSystem
{
    [Dependency] private readonly AppearanceSystem _appearanceSystem = default!;
    [Dependency] private readonly OccluderSystem _occluderSystem = default!; // DS14
    [Dependency] private readonly SpriteSystem _sprite = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<AirlockComponent, ComponentStartup>(OnComponentStartup);
        SubscribeLocalEvent<AirlockComponent, AppearanceChangeEvent>(OnAppearanceChange);
    }

    // DS14-start
    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var query = EntityQueryEnumerator<AirlockComponent, OccluderComponent, SpriteComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var airlock, out var occluder, out var sprite, out var xform))
        {
            ApplyFrontVisualOffset(uid, airlock, sprite, xform);

            if (airlock.VisualOccluderBounds is not { } bounds || occluder.BoundingBox == bounds)
                continue;

            _occluderSystem.SetBoundingBox(uid, bounds, occluder);
        }
    }

    private void ApplyFrontVisualOffset(
        EntityUid uid,
        AirlockComponent airlock,
        SpriteComponent sprite,
        TransformComponent xform)
    {
        if (airlock.FrontVisualOffset is not { } frontOffset)
            return;

        var localRotation = xform.LocalRotation;
        var offset = localRotation.GetCardinalDir() is Direction.North or Direction.South
            ? (-localRotation).RotateVec(frontOffset)
            : Vector2.Zero;

        if (sprite.Offset.EqualsApprox(offset))
            return;

        _sprite.SetOffset((uid, sprite), offset);
    }
    // DS14-end

    private void OnComponentStartup(EntityUid uid, AirlockComponent comp, ComponentStartup args)
    {
        // DS14-start
        if (TryComp<SpriteComponent>(uid, out var sprite) &&
            TryComp<TransformComponent>(uid, out var xform))
        {
            ApplyFrontVisualOffset(uid, comp, sprite, xform);
        }
        // DS14-end

        // Has to be on component startup because we don't know what order components initialize in and running this before DoorComponent inits _will_ crash.
        if (!TryComp<DoorComponent>(uid, out var door))
            return;

        if (comp.OpenUnlitVisible) // Otherwise there are flashes of the fallback sprite between clicking on the door and the door closing animation starting.
        {
            door.OpenSpriteStates.Add((DoorVisualLayers.BaseUnlit, comp.OpenSpriteState));
            door.ClosedSpriteStates.Add((DoorVisualLayers.BaseUnlit, comp.ClosedSpriteState));
        }

        ((Animation)door.OpeningAnimation).AnimationTracks.Add(new AnimationTrackSpriteFlick()
        {
            LayerKey = DoorVisualLayers.BaseUnlit,
            KeyFrames = { new AnimationTrackSpriteFlick.KeyFrame(comp.OpeningSpriteState, 0f) },
        }
        );

        ((Animation)door.ClosingAnimation).AnimationTracks.Add(new AnimationTrackSpriteFlick()
        {
            LayerKey = DoorVisualLayers.BaseUnlit,
            KeyFrames = { new AnimationTrackSpriteFlick.KeyFrame(comp.ClosingSpriteState, 0f) },
        }
        );

        door.DenyingAnimation = new Animation()
        {
            Length = TimeSpan.FromSeconds(comp.DenyAnimationTime),
            AnimationTracks =
            {
                new AnimationTrackSpriteFlick()
                {
                    LayerKey = DoorVisualLayers.BaseUnlit,
                    KeyFrames = { new AnimationTrackSpriteFlick.KeyFrame(comp.DenySpriteState, 0f) },
                }
            }
        };

        if (!comp.AnimatePanel)
            return;

        ((Animation)door.OpeningAnimation).AnimationTracks.Add(new AnimationTrackSpriteFlick()
        {
            LayerKey = WiresVisualLayers.MaintenancePanel,
            KeyFrames = { new AnimationTrackSpriteFlick.KeyFrame(comp.OpeningPanelSpriteState, 0f) },
        });

        ((Animation)door.ClosingAnimation).AnimationTracks.Add(new AnimationTrackSpriteFlick
        {
            LayerKey = WiresVisualLayers.MaintenancePanel,
            KeyFrames = { new AnimationTrackSpriteFlick.KeyFrame(comp.ClosingPanelSpriteState, 0f) },
        });
    }

    private void OnAppearanceChange(EntityUid uid, AirlockComponent comp, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        var boltedVisible = false;
        var emergencyLightsVisible = false;
        var unlitVisible = false;

        if (!_appearanceSystem.TryGetData<DoorState>(uid, DoorVisuals.State, out var state, args.Component))
            state = DoorState.Closed;

        if (_appearanceSystem.TryGetData<bool>(uid, PowerDeviceVisuals.Powered, out var powered, args.Component)
            && powered)
        {
            boltedVisible = _appearanceSystem.TryGetData<bool>(uid, DoorVisuals.BoltLights, out var lights, args.Component)
                            && lights && (state == DoorState.Closed || state == DoorState.Welded);

            emergencyLightsVisible = _appearanceSystem.TryGetData<bool>(uid, DoorVisuals.EmergencyLights, out var eaLights, args.Component) && eaLights;
            unlitVisible =
                    (state == DoorState.Closing
                ||  state == DoorState.Opening
                ||  state == DoorState.Denying
                || (state == DoorState.Open && comp.OpenUnlitVisible)
                || (_appearanceSystem.TryGetData<bool>(uid, DoorVisuals.ClosedLights, out var closedLights, args.Component) && closedLights))
                    && !boltedVisible && !emergencyLightsVisible;
        }

        _sprite.LayerSetVisible((uid, args.Sprite), DoorVisualLayers.BaseUnlit, unlitVisible);
        _sprite.LayerSetVisible((uid, args.Sprite), DoorVisualLayers.BaseBolted, boltedVisible);
        if (comp.EmergencyAccessLayer)
        {
            _sprite.LayerSetVisible(
                (uid, args.Sprite),
                DoorVisualLayers.BaseEmergencyAccess,
                    emergencyLightsVisible
                &&  state != DoorState.Open
                &&  state != DoorState.Opening
                &&  state != DoorState.Closing
                && !boltedVisible
            );
        }

        switch (state)
        {
            case DoorState.Open:
                _sprite.LayerSetRsiState((uid, args.Sprite), DoorVisualLayers.BaseUnlit, comp.OpenSpriteState); // DS14
                _sprite.LayerSetAnimationTime((uid, args.Sprite), DoorVisualLayers.BaseUnlit, 0);
                break;
            case DoorState.Closed:
                _sprite.LayerSetRsiState((uid, args.Sprite), DoorVisualLayers.BaseUnlit, comp.ClosedSpriteState); // DS14
                _sprite.LayerSetAnimationTime((uid, args.Sprite), DoorVisualLayers.BaseUnlit, 0);
                break;
        }
    }
}
