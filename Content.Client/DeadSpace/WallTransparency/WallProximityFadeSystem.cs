// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

using System.Numerics;
using Robust.Client.GameObjects;
using Robust.Client.Player;

namespace Content.Client.DeadSpace.WallTransparency;

public sealed class WallProximityFadeSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    private const float LookupRadius = 2.05f;
    private const float AlphaEpsilon = 0.005f;

    private readonly HashSet<EntityUid> _active = new();
    private readonly HashSet<EntityUid> _nearby = new();
    private readonly List<EntityUid> _toRemove = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<WallProximityFadeComponent, ComponentShutdown>(OnShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _nearby.Clear();

        if (_player.LocalEntity is not { } player ||
            !TryComp(player, out TransformComponent? playerXform))
        {
            FadeInactiveWallsToOpaque(frameTime);
            return;
        }

        var playerCoords = _transform.GetMapCoordinates(player, xform: playerXform);
        var playerWorldPos = _transform.GetWorldPosition(playerXform);

        foreach (var wall in _lookup.GetEntitiesInRange<WallProximityFadeComponent>(
                     playerCoords,
                     LookupRadius,
                     LookupFlags.Static))
        {
            if (!TryComp<SpriteComponent>(wall.Owner, out var sprite) ||
                !TryComp(wall.Owner, out TransformComponent? wallXform))
                continue;

            _nearby.Add(wall.Owner);

            var wallCoords = _transform.GetMapCoordinates(wall.Owner, xform: wallXform);
            var distance = (wallCoords.Position - playerCoords.Position).Length();
            var targetAlpha = WallOccludesPlayer(
                wall.Comp,
                wallXform,
                playerXform,
                playerWorldPos)
                ? GetTargetAlpha(wall.Comp, distance)
                : 1f;

            SetAlpha(wall.Owner, wall.Comp, sprite, frameTime, targetAlpha);

            if (wall.Comp.CurrentAlpha < 1f - AlphaEpsilon || targetAlpha < 1f - AlphaEpsilon)
                _active.Add(wall.Owner);
            else
                _active.Remove(wall.Owner);
        }

        FadeInactiveWallsToOpaque(frameTime);
    }

    private void FadeInactiveWallsToOpaque(float frameTime)
    {
        _toRemove.Clear();

        foreach (var uid in _active)
        {
            if (_nearby.Contains(uid))
                continue;

            if (!TryComp<WallProximityFadeComponent>(uid, out var fade) ||
                !TryComp<SpriteComponent>(uid, out var sprite))
            {
                _toRemove.Add(uid);
                continue;
            }

            SetAlpha(uid, fade, sprite, frameTime, 1f);

            if (fade.CurrentAlpha >= 1f - AlphaEpsilon)
                _toRemove.Add(uid);
        }

        foreach (var uid in _toRemove)
            _active.Remove(uid);
    }

    private void OnShutdown(EntityUid uid, WallProximityFadeComponent component, ComponentShutdown args)
    {
        _active.Remove(uid);
        _nearby.Remove(uid);

        if (TryComp<SpriteComponent>(uid, out var sprite))
            _sprite.SetColor((uid, sprite), sprite.Color.WithAlpha(1f));
    }

    private void SetAlpha(
        EntityUid uid,
        WallProximityFadeComponent component,
        SpriteComponent sprite,
        float frameTime,
        float targetAlpha)
    {
        var speed = MathF.Max(component.FadeSpeed, 0f);
        var weight = speed == 0f ? 1f : 1f - MathF.Exp(-speed * frameTime);
        var alpha = MathHelper.Lerp(component.CurrentAlpha, targetAlpha, weight);

        if (MathF.Abs(alpha - targetAlpha) < AlphaEpsilon)
            alpha = targetAlpha;

        component.CurrentAlpha = Math.Clamp(alpha, 0f, 1f);
        _sprite.SetColor((uid, sprite), sprite.Color.WithAlpha(component.CurrentAlpha));
    }

    private bool WallOccludesPlayer(
        WallProximityFadeComponent component,
        TransformComponent wallXform,
        TransformComponent playerXform,
        Vector2 playerWorldPos)
    {
        if (wallXform.GridUid != playerXform.GridUid)
            return false;

        var (wallWorldPos, wallWorldRot) = _transform.GetWorldPositionRotation(wallXform);
        var wallToPlayer = playerWorldPos - wallWorldPos;
        var localDelta = (-wallWorldRot).RotateVec(wallToPlayer);

        var occlusionHalfWidth = MathF.Max(component.OcclusionHalfWidth, 0f);
        if (MathF.Abs(localDelta.X) > occlusionHalfWidth)
            return false;

        return localDelta.Y > 0f;
    }

    private static float GetTargetAlpha(WallProximityFadeComponent component, float distance)
    {
        var start = MathF.Max(component.FadeStartRadius, 0.01f);
        var full = Math.Clamp(component.FadeFullRadius, 0f, start);
        var minAlpha = Math.Clamp(component.MinAlpha, 0f, 1f);

        if (distance >= start)
            return 1f;

        if (distance <= full || MathF.Abs(start - full) < 0.001f)
            return minAlpha;

        var t = (distance - full) / (start - full);
        t = t * t * (3f - 2f * t);
        return MathHelper.Lerp(minAlpha, 1f, t);
    }
}
