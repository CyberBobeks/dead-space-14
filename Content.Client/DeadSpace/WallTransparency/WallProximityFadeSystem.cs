// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

using System.Numerics;
using Content.Shared.Item;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.Client.DeadSpace.WallTransparency;

public sealed class WallProximityFadeSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    private const float ItemLookupRadius = 2.05f;
    private const float PlayerLookupRadius = 2.30f;
    private const float ItemSafetyRefreshInterval = 1f;
    private const float CameraRefreshDistanceSquared = 0.25f;
    private const float PlayerMovementEpsilonSquared = 0.0001f;
    private const float AbsoluteMinAlpha = 0.5f;
    private const float AlphaEpsilon = 0.005f;
    private const int ItemsPerFrame = 32;
    private const LookupFlags ItemLookupFlags =
        LookupFlags.Dynamic | LookupFlags.Sundries | LookupFlags.Approximate;

    private readonly Dictionary<WallTileKey, List<EntityUid>> _wallsByTile = new();
    private readonly Dictionary<EntityUid, CachedWall> _wallCache = new();
    private readonly Dictionary<EntityUid, float> _playerWallTargets = new();
    private Dictionary<EntityUid, float> _itemWallTargets = new();
    private Dictionary<EntityUid, float> _buildingItemWallTargets = new();
    private readonly Dictionary<EntityUid, WallFadeState> _wallStates = new();
    private readonly HashSet<Entity<ItemComponent>> _itemSources = new();
    private readonly List<EntityUid> _pendingItems = new();
    private readonly List<EntityUid> _toRemove = new();

    private EntityQuery<ItemComponent> _itemQuery;
    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<SpriteComponent> _spriteQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    private EntityUid _itemPassPlayer;
    private EntityUid _lastPlayer;
    private EntityUid _lastPlayerGrid;
    private MapId _lastCameraMap = MapId.Nullspace;
    private Vector2 _lastCameraPosition;
    private Vector2 _lastPlayerPosition;
    private int _pendingItemIndex;
    private int _wallIndexVersion;
    private int _lastPlayerWallIndexVersion = -1;
    private int _targetGeneration;
    private float _itemSafetyRefreshTimer;
    private bool _hasCameraPosition;
    private bool _hasPlayerPosition;
    private bool _itemPassActive;
    private bool _itemsDirty = true;

    public override void Initialize()
    {
        base.Initialize();

        _itemQuery = GetEntityQuery<ItemComponent>();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _spriteQuery = GetEntityQuery<SpriteComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        SubscribeLocalEvent<WallProximityFadeComponent, ComponentStartup>(OnWallStartup);
        SubscribeLocalEvent<WallProximityFadeComponent, MoveEvent>(OnWallMoved);
        SubscribeLocalEvent<WallProximityFadeComponent, AnchorStateChangedEvent>(OnWallAnchorChanged);
        SubscribeLocalEvent<WallProximityFadeComponent, ComponentShutdown>(OnWallShutdown);

        SubscribeLocalEvent<ItemComponent, ComponentStartup>(OnItemChanged);
        SubscribeLocalEvent<ItemComponent, MoveEvent>(OnItemMoved);
        SubscribeLocalEvent<ItemComponent, ComponentShutdown>(OnItemChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _targetGeneration++;

        if (_player.LocalEntity is { } player &&
            _xformQuery.TryGetComponent(player, out var playerXform) &&
            playerXform.MapID == _eye.CurrentMap &&
            TryGetGridPosition(
                playerXform,
                out var playerGrid,
                out var grid,
                out var playerPosition,
                out _))
        {
            UpdatePlayerTargets(player, playerGrid, grid, playerPosition);
            UpdateItemRefreshState(frameTime);

            if (_itemPassActive && _itemPassPlayer != player)
                CancelItemPass();

            if (!_itemPassActive && _itemsDirty)
                BeginItemPass(player);

            ProcessItemPass(player);
        }
        else
        {
            ResetSources();
        }

        ApplyTargets(_playerWallTargets);
        ApplyTargets(_itemWallTargets);
        AnimateTrackedWalls(frameTime);
    }

    private void UpdatePlayerTargets(
        EntityUid player,
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2 playerPosition)
    {
        if (_hasPlayerPosition &&
            _lastPlayer == player &&
            _lastPlayerGrid == gridUid &&
            _lastPlayerWallIndexVersion == _wallIndexVersion &&
            Vector2.DistanceSquared(_lastPlayerPosition, playerPosition) <= PlayerMovementEpsilonSquared)
        {
            return;
        }

        _playerWallTargets.Clear();
        AddSource(
            gridUid,
            grid,
            playerPosition,
            PlayerLookupRadius,
            isPlayer: true,
            _playerWallTargets);

        _lastPlayer = player;
        _lastPlayerGrid = gridUid;
        _lastPlayerPosition = playerPosition;
        _lastPlayerWallIndexVersion = _wallIndexVersion;
        _hasPlayerPosition = true;
    }

    private void UpdateItemRefreshState(float frameTime)
    {
        _itemSafetyRefreshTimer -= frameTime;
        if (_itemSafetyRefreshTimer <= 0f)
        {
            _itemSafetyRefreshTimer = ItemSafetyRefreshInterval;
            _itemsDirty = true;
        }

        var cameraPosition = _eye.GetWorldViewport().Center;
        if (!_hasCameraPosition ||
            _lastCameraMap != _eye.CurrentMap ||
            Vector2.DistanceSquared(_lastCameraPosition, cameraPosition) >= CameraRefreshDistanceSquared)
        {
            _lastCameraMap = _eye.CurrentMap;
            _lastCameraPosition = cameraPosition;
            _hasCameraPosition = true;
            _itemsDirty = true;
        }
    }

    private void BeginItemPass(EntityUid player)
    {
        _itemsDirty = false;
        _itemPassActive = true;
        _itemPassPlayer = player;
        _pendingItemIndex = 0;
        _pendingItems.Clear();
        _buildingItemWallTargets.Clear();
        _itemSources.Clear();

        var viewport = _eye.GetWorldViewport().Enlarged(ItemLookupRadius);
        _lookup.GetEntitiesIntersecting(
            _eye.CurrentMap,
            viewport,
            _itemSources,
            ItemLookupFlags);

        foreach (var item in _itemSources)
        {
            _pendingItems.Add(item.Owner);
        }

        if (_pendingItems.Count == 0)
            FinishItemPass();
    }

    private void ProcessItemPass(EntityUid player)
    {
        if (!_itemPassActive)
            return;

        var end = Math.Min(_pendingItemIndex + ItemsPerFrame, _pendingItems.Count);
        for (; _pendingItemIndex < end; _pendingItemIndex++)
        {
            var item = _pendingItems[_pendingItemIndex];
            if (item == player ||
                !IsPickupableWorldItem(item, out var itemXform) ||
                itemXform.MapID != _eye.CurrentMap ||
                !TryGetGridPosition(
                    itemXform,
                    out var gridUid,
                    out var grid,
                    out var itemPosition,
                    out _))
            {
                continue;
            }

            AddSource(
                gridUid,
                grid,
                itemPosition,
                ItemLookupRadius,
                isPlayer: false,
                _buildingItemWallTargets);
        }

        if (_pendingItemIndex >= _pendingItems.Count)
            FinishItemPass();
    }

    private void FinishItemPass()
    {
        (_itemWallTargets, _buildingItemWallTargets) =
            (_buildingItemWallTargets, _itemWallTargets);
        _buildingItemWallTargets.Clear();
        _pendingItems.Clear();
        _pendingItemIndex = 0;
        _itemPassActive = false;
    }

    private void CancelItemPass()
    {
        _buildingItemWallTargets.Clear();
        _pendingItems.Clear();
        _pendingItemIndex = 0;
        _itemPassActive = false;
        _itemsDirty = true;
    }

    private void ResetSources()
    {
        _playerWallTargets.Clear();
        _itemWallTargets.Clear();
        _hasPlayerPosition = false;
        _hasCameraPosition = false;
        _itemSafetyRefreshTimer = 0f;
        _itemsDirty = true;

        if (_itemPassActive)
            CancelItemPass();
    }

    private bool IsPickupableWorldItem(EntityUid uid, out TransformComponent xform)
    {
        if (!_itemQuery.HasComponent(uid) ||
            !_xformQuery.TryGetComponent(uid, out var itemXform))
        {
            xform = default!;
            return false;
        }

        xform = itemXform;
        if (xform.Anchored ||
            xform.MapID == MapId.Nullspace ||
            (xform.ParentUid != xform.GridUid && xform.ParentUid != xform.MapUid) ||
            !_spriteQuery.TryGetComponent(uid, out var sprite) ||
            !sprite.Visible)
        {
            return false;
        }

        // This mirrors the structural pickup guard in SharedHandsSystem without running
        // per-item hand checks or cancellable interaction events on the render client.
        return !_physicsQuery.TryGetComponent(uid, out var physics) ||
               physics.BodyType != BodyType.Static;
    }

    private void AddSource(
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2 sourcePosition,
        float lookupRadius,
        bool isPlayer,
        Dictionary<EntityUid, float> targets)
    {
        var sourceTile = _map.TileIndicesFor(
            gridUid,
            grid,
            new EntityCoordinates(gridUid, sourcePosition));
        var tileRadius = (int) Math.Ceiling(lookupRadius / grid.TileSize);
        var lookupRadiusSquared = lookupRadius * lookupRadius;

        for (var x = -tileRadius; x <= tileRadius; x++)
        {
            for (var y = -tileRadius; y <= tileRadius; y++)
            {
                var key = new WallTileKey(gridUid, sourceTile + new Vector2i(x, y));
                if (!_wallsByTile.TryGetValue(key, out var walls))
                    continue;

                foreach (var wall in walls)
                {
                    if (!_wallCache.TryGetValue(wall, out var cachedWall))
                        continue;

                    var wallToSource = sourcePosition - cachedWall.LocalPosition;
                    var distanceSquared = wallToSource.LengthSquared();
                    if (distanceSquared >= lookupRadiusSquared)
                        continue;

                    var radiusBonus = isPlayer
                        ? MathF.Max(cachedWall.Component.PlayerRadiusBonus, 0f)
                        : 0f;
                    var startRadius = MathF.Max(
                        cachedWall.Component.FadeStartRadius + radiusBonus,
                        0.01f);

                    if (distanceSquared >= startRadius * startRadius ||
                        !WallOccludesSource(
                            cachedWall.Component,
                            cachedWall.OcclusionRotation,
                            wallToSource))
                    {
                        continue;
                    }

                    var targetAlpha = GetTargetAlpha(
                        cachedWall.Component,
                        MathF.Sqrt(distanceSquared),
                        radiusBonus);

                    // Sources never stack their opacity. The strongest single fade wins.
                    SetStrongestTarget(targets, wall, targetAlpha);
                }
            }
        }
    }

    private void ApplyTargets(Dictionary<EntityUid, float> targets)
    {
        foreach (var (wall, targetAlpha) in targets)
        {
            if (_wallStates.TryGetValue(wall, out var state) &&
                state.Generation == _targetGeneration)
            {
                state.TargetAlpha = MathF.Min(state.TargetAlpha, targetAlpha);
                _wallStates[wall] = state;
            }
            else
            {
                _wallStates[wall] = new WallFadeState(targetAlpha, _targetGeneration);
            }
        }
    }

    private static void SetStrongestTarget(
        Dictionary<EntityUid, float> targets,
        EntityUid wall,
        float targetAlpha)
    {
        if (targets.TryGetValue(wall, out var current))
            targets[wall] = MathF.Min(current, targetAlpha);
        else
            targets.Add(wall, targetAlpha);
    }

    private void AnimateTrackedWalls(float frameTime)
    {
        _toRemove.Clear();

        foreach (var (wall, state) in _wallStates)
        {
            if (!TryComp(wall, out WallProximityFadeComponent? fade) ||
                !_spriteQuery.TryGetComponent(wall, out var sprite))
            {
                _toRemove.Add(wall);
                continue;
            }

            var targetAlpha = state.Generation == _targetGeneration
                ? state.TargetAlpha
                : 1f;
            SetAlpha(wall, fade, sprite, frameTime, targetAlpha);

            if (targetAlpha >= 1f - AlphaEpsilon &&
                fade.CurrentAlpha >= 1f - AlphaEpsilon)
            {
                _toRemove.Add(wall);
            }
        }

        foreach (var wall in _toRemove)
        {
            _wallStates.Remove(wall);
        }
    }

    private void OnWallStartup(
        EntityUid uid,
        WallProximityFadeComponent component,
        ComponentStartup args)
    {
        IndexWall(uid, component, Transform(uid));
    }

    private void OnWallMoved(
        EntityUid uid,
        WallProximityFadeComponent component,
        ref MoveEvent args)
    {
        IndexWall(uid, component, args.Component);
    }

    private void OnWallAnchorChanged(
        EntityUid uid,
        WallProximityFadeComponent component,
        ref AnchorStateChangedEvent args)
    {
        IndexWall(uid, component, args.Transform);
    }

    private void OnWallShutdown(
        EntityUid uid,
        WallProximityFadeComponent component,
        ComponentShutdown args)
    {
        RemoveWallFromIndex(uid);
        MarkWallIndexChanged();

        _playerWallTargets.Remove(uid);
        _itemWallTargets.Remove(uid);
        _buildingItemWallTargets.Remove(uid);
        _wallStates.Remove(uid);

        if (_spriteQuery.TryGetComponent(uid, out var sprite))
            _sprite.SetColor((uid, sprite), sprite.Color.WithAlpha(1f));
    }

    private void IndexWall(
        EntityUid uid,
        WallProximityFadeComponent component,
        TransformComponent xform)
    {
        RemoveWallFromIndex(uid);

        if (!xform.Anchored ||
            !TryGetGridPosition(
                xform,
                out var gridUid,
                out var grid,
                out var localPosition,
                out var localRotation))
        {
            MarkWallIndexChanged();
            return;
        }

        var tile = _map.TileIndicesFor(
            gridUid,
            grid,
            new EntityCoordinates(gridUid, localPosition));
        var key = new WallTileKey(gridUid, tile);
        var occlusionRotation = component.UseLocalRotation
            ? localRotation
            : Angle.Zero;

        _wallCache.Add(
            uid,
            new CachedWall(
                localPosition,
                occlusionRotation,
                component,
                key));

        if (!_wallsByTile.TryGetValue(key, out var walls))
        {
            walls = new List<EntityUid>(1);
            _wallsByTile.Add(key, walls);
        }

        walls.Add(uid);
        MarkWallIndexChanged();
    }

    private void RemoveWallFromIndex(EntityUid uid)
    {
        if (!_wallCache.Remove(uid, out var cachedWall) ||
            !_wallsByTile.TryGetValue(cachedWall.TileKey, out var walls))
        {
            return;
        }

        walls.Remove(uid);
        if (walls.Count == 0)
            _wallsByTile.Remove(cachedWall.TileKey);
    }

    private void MarkWallIndexChanged()
    {
        _wallIndexVersion++;
        _itemsDirty = true;
    }

    private void OnItemChanged(EntityUid uid, ItemComponent component, ComponentStartup args)
    {
        _itemsDirty = true;
    }

    private void OnItemChanged(EntityUid uid, ItemComponent component, ComponentShutdown args)
    {
        _itemsDirty = true;
    }

    private void OnItemMoved(EntityUid uid, ItemComponent component, ref MoveEvent args)
    {
        _itemsDirty = true;
    }

    private bool TryGetGridPosition(
        TransformComponent xform,
        out EntityUid gridUid,
        out MapGridComponent grid,
        out Vector2 localPosition,
        out Angle localRotation)
    {
        if (xform.GridUid is not { } uid ||
            xform.MapID == MapId.Nullspace ||
            !_gridQuery.TryGetComponent(uid, out var gridComponent))
        {
            gridUid = default;
            grid = default!;
            localPosition = default;
            localRotation = default;
            return false;
        }

        gridUid = uid;
        grid = gridComponent;

        if (xform.ParentUid == gridUid)
        {
            localPosition = xform.Coordinates.Position;
            localRotation = xform.LocalRotation;
            return true;
        }

        (localPosition, localRotation) =
            _transform.GetRelativePositionRotation(xform, gridUid);
        return true;
    }

    private void SetAlpha(
        EntityUid uid,
        WallProximityFadeComponent component,
        SpriteComponent sprite,
        float frameTime,
        float targetAlpha)
    {
        targetAlpha = Math.Clamp(targetAlpha, AbsoluteMinAlpha, 1f);

        if (MathF.Abs(component.CurrentAlpha - targetAlpha) < AlphaEpsilon)
        {
            if (component.CurrentAlpha == targetAlpha)
                return;

            component.CurrentAlpha = targetAlpha;
            _sprite.SetColor((uid, sprite), sprite.Color.WithAlpha(targetAlpha));
            return;
        }

        var speed = MathF.Max(component.FadeSpeed, 0f);
        var weight = speed == 0f ? 1f : 1f - MathF.Exp(-speed * frameTime);
        var alpha = MathHelper.Lerp(component.CurrentAlpha, targetAlpha, weight);

        if (MathF.Abs(alpha - targetAlpha) < AlphaEpsilon)
            alpha = targetAlpha;

        component.CurrentAlpha = Math.Clamp(alpha, AbsoluteMinAlpha, 1f);
        _sprite.SetColor((uid, sprite), sprite.Color.WithAlpha(component.CurrentAlpha));
    }

    private static bool WallOccludesSource(
        WallProximityFadeComponent component,
        Angle occlusionRotation,
        Vector2 wallToSource)
    {
        var localDelta = (-occlusionRotation).RotateVec(wallToSource);
        var occlusionHalfWidth = MathF.Max(component.OcclusionHalfWidth, 0f);
        var lateralDistance = MathF.Abs(localDelta.X);
        if (lateralDistance > occlusionHalfWidth)
            return false;

        // The source must be meaningfully behind the wall, not merely a fraction
        // above its center while standing beside it.
        return localDelta.Y > lateralDistance;
    }

    private static float GetTargetAlpha(
        WallProximityFadeComponent component,
        float distance,
        float radiusBonus)
    {
        var start = MathF.Max(component.FadeStartRadius + radiusBonus, 0.01f);
        var full = Math.Clamp(component.FadeFullRadius + radiusBonus, 0f, start);
        var minAlpha = Math.Clamp(component.MinAlpha, AbsoluteMinAlpha, 1f);

        if (distance >= start)
            return 1f;

        if (distance <= full || MathF.Abs(start - full) < 0.001f)
            return minAlpha;

        var t = (distance - full) / (start - full);
        t = t * t * (3f - 2f * t);
        return MathHelper.Lerp(minAlpha, 1f, t);
    }

    private readonly record struct WallTileKey(EntityUid GridUid, Vector2i Tile);

    private readonly record struct CachedWall(
        Vector2 LocalPosition,
        Angle OcclusionRotation,
        WallProximityFadeComponent Component,
        WallTileKey TileKey);

    private struct WallFadeState(float targetAlpha, int generation)
    {
        public float TargetAlpha = targetAlpha;
        public int Generation = generation;
    }
}
