// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

using System.Numerics;
using Content.Client.Graphics;
using Content.Shared.Ghost;
using Content.Shared.Humanoid;
using Content.Shared.Light.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Physics;
using Content.Shared.Stealth;
using Content.Shared.Stealth.Components;
using Robust.Client.GameObjects;
using Robust.Client.ComponentTrees;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Graphics;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;

namespace Content.Client.DeadSpace.Lighting;

public sealed class RayLightingOverlay : Overlay
{
    private static readonly ProtoId<ShaderPrototype> GeometryId = "RayLightGeometry";
    private static readonly ProtoId<ShaderPrototype> SeedId = "RayLightSeed";
    private static readonly ProtoId<ShaderPrototype> JumpId = "RayLightJump";
    private static readonly ProtoId<ShaderPrototype> DirectId = "RayLightDirect";
    private static readonly ProtoId<ShaderPrototype> IndirectId = "RayLightIndirect";
    private static readonly ProtoId<ShaderPrototype> ComposeId = "RayLightCompose";
    private static readonly ProtoId<ShaderPrototype> ActorShadowId = "RayLightActorShadow";
    private static readonly ProtoId<ShaderPrototype> CombineId = "RayLightCombine";
    private static readonly Color EmptyField = new(0f, 0f, 0f, 0f);
    private const float ObstacleHeight = 3.5f;

    [Dependency] private readonly IClyde _clyde = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly ILightManager _lighting = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IResourceCache _textures = default!;

    private readonly SharedTransformSystem _transform;
    private readonly SharedPhysicsSystem _physics;
    private readonly EntityLookupSystem _lookup;
    private readonly OccluderSystem _occluders;
    private readonly SharedStealthSystem _stealth;
    private readonly SpriteSystem _sprites;
    private readonly LightTreeSystem _lightTree;
    private readonly RayLightingSystem _system;
    private readonly OverlayResourceCache<CachedResources> _resources = new();
    private readonly HashSet<Entity<PhysicsComponent>> _bodies = new();
    private readonly List<LightSource> _sources = new();
    private readonly List<ActorCaster> _casters = new();
    private readonly HashSet<Entity<PointLightComponent, TransformComponent>> _visibleLights = new();
    private readonly HashSet<Entity<OccluderComponent, TransformComponent>> _visibleOccluders = new();
    private readonly List<Geometry> _geometry = new();

    public override OverlaySpace Space => OverlaySpace.BeforeLighting;

    public RayLightingOverlay()
    {
        IoCManager.InjectDependencies(this);
        _transform = _entities.System<SharedTransformSystem>();
        _physics = _entities.System<SharedPhysicsSystem>();
        _lookup = _entities.System<EntityLookupSystem>();
        _occluders = _entities.System<OccluderSystem>();
        _stealth = _entities.System<SharedStealthSystem>();
        _sprites = _entities.System<SpriteSystem>();
        _lightTree = _entities.System<LightTreeSystem>();
        _system = _entities.System<RayLightingSystem>();
        // Existing roof/planet layers finish first. Native FOV and wall lighting run afterwards.
        ZIndex = 100;
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        return _system.Enabled && _lighting.Enabled && _lighting.DrawLighting &&
               args.Viewport.Eye is { DrawLight: true };
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var viewport = args.Viewport;
        var eye = viewport.Eye;
        if (eye == null)
            return;

        _sources.Clear();
        var viewBounds = args.WorldBounds.CalcBoundingBox();
        _visibleLights.Clear();
        _lightTree.QueryAabb(_visibleLights, args.MapId, viewBounds);
        foreach (var (uid, light, xform) in _visibleLights)
        {
            if (xform.MapID != args.MapId || !light.Enabled || light.ContainerOccluded ||
                light.Energy <= 0f || !RayLightingSystem.CanRender(light))
                continue;

            var (position, rotation) = _transform.GetWorldPositionRotation(xform);
            position += rotation.RotateVec(light.Offset);
            if (!new Circle(position, light.Radius).Intersects(viewBounds))
                continue;

            var fixture = xform.Anchored || _entities.HasComponent<PoweredLightComponent>(uid);
            var height = fixture ? 3.0f : 1.25f;
            var maskRotation = light.Rotation + (light.MaskAutoRotate ? rotation : Angle.Zero);
            _sources.Add(new LightSource(light, position, maskRotation, height,
                rotation.RotateVec(Vector2.UnitX), fixture ? 0.18f : 0.04f));
        }
        if (_sources.Count == 0)
            return;

        // World-aligned cells keep the obstacle field stable while the camera moves.
        var expanded = viewBounds.Enlarged(RayLightingSystem.MaxRadius + 1f);
        var density = Math.Min(8f, 1020f / Math.Max(expanded.Width, expanded.Height));
        // Keep the geometry cache stable during small camera movements.
        var origin = new Vector2(MathF.Floor(expanded.Left / 4f) * 4f,
            MathF.Floor(expanded.Bottom / 4f) * 4f);
        var size = new Vector2i((int) MathF.Ceiling((expanded.Width + 4f) * density / 32f - 0.0001f) * 32,
            (int) MathF.Ceiling((expanded.Height + 4f) * density / 32f - 0.0001f) * 32);
        var worldSize = (Vector2) size / density;
        var fieldBounds = Box2.FromDimensions(origin, worldSize);
        var fieldMatrix = new Matrix3x2(density, 0f, 0f, -density, -origin.X * density,
            (origin.Y + worldSize.Y) * density);
        var handle = args.WorldHandle;
        var renderHandle = args.RenderHandle;
        var res = _resources.GetForViewport(viewport, _ => new CachedResources(_prototypes));
        var radianceScale = Math.Clamp((int) MathF.Ceiling(viewport.LightRenderTarget.Size.Y / (viewBounds.Height * density) - 0.001f), 1, 4);
        // Trace only the visible floor and the short bounce margin. The larger obstacle field
        // is needed for paths to offscreen lamps, but must not inflate every lamp's light buffers.
        var visible = viewBounds.Enlarged(3f);
        var renderOrigin = new Vector2(MathF.Floor(visible.Left * density) / density,
            MathF.Floor(visible.Bottom * density) / density);
        // Bucket dimensions and ignore subpixel float noise, avoiding texture reallocations while panning.
        var renderSize = new Vector2i((int) MathF.Ceiling((visible.Width * density + 2f) / 16f - 0.0001f) * 16,
            (int) MathF.Ceiling((visible.Height * density + 2f) / 16f - 0.0001f) * 16);
        var renderWorldSize = (Vector2) renderSize / density;
        var renderBounds = Box2.FromDimensions(renderOrigin, renderWorldSize);
        var renderMatrix = new Matrix3x2(density, 0f, 0f, -density, -renderOrigin.X * density,
            (renderOrigin.Y + renderWorldSize.Y) * density);
        res.EnsureTargets(_clyde, size, renderSize, radianceScale);
        var radianceMatrix = renderMatrix * Matrix3x2.CreateScale(radianceScale);

        _bodies.Clear();
        _casters.Clear();
        _lookup.GetEntitiesIntersecting(args.MapId, fieldBounds, _bodies);
        var mapId = args.MapId;
        var viewOrigin = viewport.LocalToWorld(viewport.Size / 2f).Position;
        var viewUp = Vector2.Normalize(viewport.LocalToWorld(new Vector2(viewport.Size.X / 2f, 0f)).Position - viewOrigin);
        var geometryHash = 0;
        _geometry.Clear();
        foreach (var body in _bodies)
        {
            if (!_entities.TryGetComponent<SpriteComponent>(body.Owner, out var sprite) ||
                !sprite.Visible || sprite.ContainerOccluded || sprite.Color.A < 0.3f ||
                _entities.HasComponent<GhostComponent>(body.Owner) ||
                _entities.HasComponent<OccluderComponent>(body.Owner))
                continue;
            if (_entities.TryGetComponent<StealthComponent>(body.Owner, out var stealth) &&
                stealth.Enabled && _stealth.GetVisibility(body.Owner, stealth) < 0.5f)
                continue;

            var mob = _entities.HasComponent<MobStateComponent>(body.Owner);
            if (mob)
            {
                if (_casters.Count >= 64)
                    continue;
                var xform = _entities.GetComponent<TransformComponent>(body.Owner);
                var (position, rotation) = _transform.GetWorldPositionRotation(xform);
                var renderRotation = rotation + eye.Rotation;
                var spriteBounds = _sprites.CalculateBounds((body.Owner, sprite), Vector2.Zero, renderRotation, Angle.Zero).CalcBoundingBox();
                if (spriteBounds.Width < 0.05f || spriteBounds.Height < 0.05f)
                    continue;
                var index = _casters.Count;
                var captureScale = Math.Min(62f / (spriteBounds.Width * EyeManager.PixelsPerMeter),
                    94f / (spriteBounds.Height * EyeManager.PixelsPerMeter));
                var pixels = spriteBounds.Size * EyeManager.PixelsPerMeter * captureScale;
                var cell = new Vector2(index % 8 * 64 + 1, index / 8 * 96 + 1);
                var drawPosition = cell + new Vector2(-spriteBounds.Left, spriteBounds.Top) * EyeManager.PixelsPerMeter * captureScale;
                var atlas = new Vector4(cell.X / 512f, (768f - cell.Y - pixels.Y) / 768f, pixels.X / 512f, pixels.Y / 768f);
                // Keep the ground anchor fixed when clothing, held items or animation change the sprite bounds.
                var footOffset = _entities.HasComponent<HumanoidAppearanceComponent>(body.Owner)
                    ? -0.5f * sprite.Scale.Y : spriteBounds.Bottom;
                var anchor = new Vector2(-spriteBounds.Left / spriteBounds.Width,
                    (footOffset - spriteBounds.Bottom) / spriteBounds.Height);
                _casters.Add(new ActorCaster(body.Owner, sprite, renderRotation, drawPosition, captureScale,
                    position + viewUp * footOffset, spriteBounds.Width,
                    Math.Clamp(1.4f * (spriteBounds.Top - footOffset), 0.6f, 2.2f),
                    1.4f * spriteBounds.Height, anchor, atlas));
                continue;
            }
            var solidFurniture = body.Comp.BodyType == BodyType.Static && body.Comp.CanCollide &&
                                 (body.Comp.CollisionLayer & (int) CollisionGroup.MobMask) != 0;
            if (!solidFurniture && body.Comp.BodyType == BodyType.Static)
                continue;
            var bounds = _physics.GetWorldAABB(body.Owner, body: body.Comp);
            if (bounds.Width < 0.15f || bounds.Height < 0.15f)
                continue;
            var height = solidFurniture ? 1.0f :
                Math.Clamp(Math.Max(bounds.Width, bounds.Height) * 0.65f, 0.2f, 1.25f);
            var tint = sprite.Color;
            var colour = Color.ToSrgb(new Color(height / ObstacleHeight, tint.R * 0.4f, tint.G * 0.4f, tint.B * 0.4f));
            geometryHash ^= HashCode.Combine(body.Owner, bounds, colour);
            _geometry.Add(new Geometry(bounds, Matrix3x2.Identity, colour));
        }

        // Full-height structures win over low furniture and moving bodies at shared edges.
        _visibleOccluders.Clear();
        _occluders.QueryAabb(_visibleOccluders, mapId, fieldBounds);
        foreach (var (uid, occluder, xform) in _visibleOccluders)
        {
            var matrix = _transform.GetWorldMatrix(xform);
            var tint = _entities.TryGetComponent<SpriteComponent>(uid, out var sprite)
                ? sprite.Color : Color.White;
            var colour = Color.ToSrgb(new Color(1f, tint.R * 0.45f, tint.G * 0.45f, tint.B * 0.45f));
            geometryHash ^= HashCode.Combine(uid, occluder.BoundingBox, matrix, colour);
            _geometry.Add(new Geometry(occluder.BoundingBox, matrix, colour));
        }
        if (_casters.Count > 0)
        {
            renderHandle.RenderInRenderTarget(res.ActorAtlas!, () =>
            {
                handle.SetTransform(Matrix3x2.Identity);
                foreach (var caster in _casters)
                    renderHandle.DrawEntity(caster.Uid, caster.DrawPosition, new Vector2(caster.DrawScale),
                        caster.Rotation, Angle.Zero, sprite: caster.Sprite);
                handle.SetTransform(Matrix3x2.Identity);
            }, EmptyField);
        }

        if (res.Nearest == null || res.GeometryHash != geometryHash || res.FieldOrigin != origin || res.FieldSize != worldSize)
        {
#if DEBUG
            CacheRebuilds++;
#endif
            handle.RenderInRenderTarget(res.Obstacles!, () =>
            {
                handle.UseShader(res.Geometry);
                foreach (var geometry in _geometry)
                {
                    handle.SetTransform(geometry.Matrix * fieldMatrix);
                    handle.DrawRect(geometry.Bounds, geometry.Colour);
                }
                handle.UseShader(null);
                handle.SetTransform(Matrix3x2.Identity);
            }, EmptyField);
            res.Seed.SetParameter("OBSTACLES", res.Obstacles!.Texture);
            DrawField(handle, res.SeedsA!, res.Seed, fieldMatrix, fieldBounds);
            var nearest = res.SeedsA!;
            var spare = res.SeedsB!;
            var jump = 1;
            while (jump < Math.Max(size.X, size.Y))
                jump *= 2;
            res.Jump.SetParameter("FieldSize", worldSize);
            for (jump /= 2; jump >= 1; jump /= 2)
            {
                res.Jump.SetParameter("SEEDS", nearest.Texture);
                res.Jump.SetParameter("Jump", (float) jump);
                DrawField(handle, spare, res.Jump, fieldMatrix, fieldBounds);
                (nearest, spare) = (spare, nearest);
            }
            res.Nearest = nearest;
            res.GeometryHash = geometryHash;
            res.FieldOrigin = origin;
            res.FieldSize = worldSize;
        }

        var soft = _config.GetCVar(CVars.LightSoftShadows);
        SetField(res.DirectShader, res.Nearest!, res.Obstacles!, origin, worldSize, 1f / density);
        res.DirectShader.SetParameter("RenderOrigin", renderOrigin);
        res.DirectShader.SetParameter("RenderSize", renderWorldSize);
        // One silhouette per actor, driven by the combined light field. Selecting a nearest lamp
        // would snap at room boundaries; independent projections produce multiple walking figures.
        handle.RenderInRenderTarget(res.ActorShadows!, () =>
        {
            handle.SetTransform(radianceMatrix);
            for (var index = 0; index < _casters.Count; index++)
            {
                var caster = _casters[index];
                var weightedOffset = Vector2.Zero;
                var weightedDirection = Vector2.Zero;
                var weightedHeight = 0f;
                var totalWeight = 0f;
                foreach (var source in _sources)
                {
                    if (!source.Light.CastShadows || source.Height <= caster.Height + 0.1f)
                        continue;
                    var delta = source.Position - caster.Foot;
                    var distanceSquared = delta.LengthSquared();
                    var cutoff = Math.Max(0f, 1f - distanceSquared / (source.Light.Radius * source.Light.Radius));
                    if (cutoff <= 0f)
                        continue;
                    var distance = MathF.Sqrt(distanceSquared);
                    // Lights across a wall must not turn the silhouette toward an unlit neighbouring room.
                    if (distance > 0.6f && _occluders.IntersectRay(mapId,
                            new Ray(caster.Foot, delta / distance), distance - 0.55f) != null)
                        continue;
                    var colour = source.Light.Color;
                    var luminance = colour.R * 0.2126f + colour.G * 0.7152f + colour.B * 0.0722f;
                    var weight = source.Light.Energy * luminance * cutoff * cutoff *
                                 source.Height * source.Height / (distanceSquared + source.Height * source.Height);
                    weightedOffset += delta * weight;
                    weightedHeight += source.Height * weight;
                    weightedDirection += delta / Math.Max(0.1f, distance) * weight;
                    totalWeight += weight;
                }
                if (totalWeight < 0.001f)
                    continue;
                var lightPosition = caster.Foot + weightedOffset / totalWeight;
                var lightHeight = weightedHeight / totalWeight;
                var toward = lightPosition - caster.Foot;
                var coherence = Math.Clamp(weightedDirection.Length() / totalWeight, 0f, 1f);
                var direction = toward.LengthSquared() > 0.01f
                    ? Vector2.Normalize(toward) : viewUp;
                var side = new Vector2(-direction.Y, direction.X) * caster.Width;
                var ratio = lightHeight / Math.Max(0.1f, lightHeight - caster.Height);
                var a = caster.Foot - side * caster.Anchor.X;
                var b = caster.Foot + side * (1f - caster.Anchor.X);
                var c = lightPosition + (a - lightPosition) * ratio;
                var d = lightPosition + (b - lightPosition) * ratio;
                var bounds = new Box2(Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d)),
                    Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d))).Enlarged(0.3f);
                var shadow = res.ActorShaders[index] ??= _prototypes.Index(ActorShadowId).InstanceUnique();
                shadow.SetParameter("ACTOR_ATLAS", res.ActorAtlas!.Texture);
                shadow.SetParameter("RenderOrigin", renderOrigin);
                shadow.SetParameter("RenderSize", renderWorldSize);
                shadow.SetParameter("LightPosition", lightPosition);
                shadow.SetParameter("LightHeight", lightHeight);
                shadow.SetParameter("FootPosition", caster.Foot);
                shadow.SetParameter("BodyWidth", caster.Width);
                shadow.SetParameter("BodyHeight", caster.TextureHeight);
                shadow.SetParameter("BodyAnchor", caster.Anchor);
                shadow.SetParameter("ShadowStrength", 0.85f * coherence * totalWeight / (totalWeight + 0.08f));
                shadow.SetParameter("AtlasRegion", caster.Region);
                handle.UseShader(shadow);
                handle.DrawRect(bounds, Color.White);
            }
            handle.UseShader(null);
            handle.SetTransform(Matrix3x2.Identity);
        }, Color.White);
        if (_casters.Count > 0)
            _clyde.BlurRenderTarget(viewport, res.ActorShadows!, res.ActorBlur!, eye, soft ? 3f : 1f);

        handle.RenderInRenderTarget(res.Direct!, () => { }, EmptyField);
        handle.RenderInRenderTarget(res.Fill!, () => { }, EmptyField);
        foreach (var source in _sources)
        {
            var sourceSize = soft ? source.Size * Math.Max(0.1f, source.Light.Softness) : 0f;
            var shader = res.DirectShader;
            handle.RenderInRenderTarget(res.SourceLight!, () =>
            {
                handle.SetTransform(renderMatrix);
                shader.SetParameter("LightPosition", source.Position);
                shader.SetParameter("LightColor", new Vector3(source.Light.Color.R, source.Light.Color.G, source.Light.Color.B));
                shader.SetParameter("LightRadius", source.Light.Radius);
                shader.SetParameter("LightEnergy", source.Light.Energy);
                shader.SetParameter("LightHeight", source.Height);
                shader.SetParameter("SourceAxis", source.Axis);
                shader.SetParameter("SourceSize", sourceSize);
                shader.SetParameter("LightSoftness", soft ? Math.Max(0.1f, source.Light.Softness) : 0f);
                shader.SetParameter("MaskRotation", new Vector2(MathF.Cos((float) source.Rotation.Theta),
                    MathF.Sin((float) source.Rotation.Theta)));
                shader.SetParameter("HasMask", source.Light.MaskPath != null);
                shader.SetParameter("CastShadows", source.Light.CastShadows);
                shader.SetParameter("LIGHT_MASK", _textures.GetResource<TextureResource>(source.Light.MaskPath ?? RayLightingSystem.BlackMask).Texture);
                handle.UseShader(shader);
                handle.DrawRect(Box2.CenteredAround(source.Position, new Vector2(source.Light.Radius * 2f)), Color.White);
                handle.UseShader(null);
                handle.SetTransform(Matrix3x2.Identity);
            }, EmptyField);

            res.Combine.SetParameter("SOURCE_LIGHT", res.SourceLight!.Texture);
            handle.RenderInRenderTarget(source.Light.CastShadows ? res.Direct! : res.Fill!, () =>
            {
                handle.SetTransform(source.Light.CastShadows ? radianceMatrix : renderMatrix);
                handle.UseShader(res.Combine);
                handle.DrawRect(Box2.CenteredAround(source.Position, new Vector2(source.Light.Radius * 2f)), Color.White);
                handle.UseShader(null);
                handle.SetTransform(Matrix3x2.Identity);
            }, null);
        }

        SetField(res.IndirectShader, res.Nearest!, res.Obstacles!, origin, worldSize, 1f / density);
        res.IndirectShader.SetParameter("RenderOrigin", renderOrigin);
        res.IndirectShader.SetParameter("RenderSize", renderWorldSize);
        res.IndirectShader.SetParameter("DIRECT_LIGHT", res.Direct!.Texture);
        res.IndirectShader.SetParameter("FILL_LIGHT", res.Fill!.Texture);
        res.IndirectShader.SetParameter("ACTOR_SHADOWS", res.ActorShadows!.Texture);
        DrawField(handle, res.Indirect!, res.IndirectShader, renderMatrix, renderBounds);

        res.Compose.SetParameter("ViewOrigin", viewOrigin);
        res.Compose.SetParameter("ViewXAxis", viewport.LocalToWorld(new Vector2(viewport.Size.X, viewport.Size.Y / 2f)).Position - viewOrigin);
        res.Compose.SetParameter("ViewYAxis", viewport.LocalToWorld(new Vector2(viewport.Size.X / 2f, 0f)).Position - viewOrigin);
        res.Compose.SetParameter("FieldOrigin", renderOrigin);
        res.Compose.SetParameter("FieldSize", renderWorldSize);
        res.Compose.SetParameter("DIRECT_LIGHT", res.Direct.Texture);
        res.Compose.SetParameter("FILL_LIGHT", res.Fill.Texture);
        res.Compose.SetParameter("ACTOR_SHADOWS", res.ActorShadows!.Texture);
        res.Compose.SetParameter("INDIRECT_LIGHT", res.Indirect!.Texture);
        var target = viewport.LightRenderTarget;
        var scale = viewport.RenderScale * (target.Size / (Vector2) viewport.Size);
        var worldBounds = args.WorldBounds;
        handle.RenderInRenderTarget(target, () =>
        {
            handle.SetTransform(target.GetWorldToLocalMatrix(eye, scale));
            handle.UseShader(res.Compose);
            handle.DrawRect(worldBounds, Color.White);
            handle.UseShader(null);
            handle.SetTransform(Matrix3x2.Identity);
        }, null);
    }

    private static void SetField(ShaderInstance shader, IRenderTexture field, IRenderTexture obstacles,
        Vector2 origin, Vector2 size, float worldPixel)
    {
        shader.SetParameter("DISTANCE_FIELD", field.Texture);
        shader.SetParameter("OBSTACLES", obstacles.Texture);
        shader.SetParameter("FieldOrigin", origin);
        shader.SetParameter("FieldSize", size);
        shader.SetParameter("WorldPixel", worldPixel);
        shader.SetParameter("ObstacleHeight", ObstacleHeight);
    }

    private static void DrawField(DrawingHandleWorld handle, IRenderTexture target, ShaderInstance shader,
        Matrix3x2 matrix, Box2 bounds)
    {
        handle.RenderInRenderTarget(target, () =>
        {
            handle.SetTransform(matrix);
            handle.UseShader(shader);
            handle.DrawRect(bounds, Color.White);
            handle.UseShader(null);
            handle.SetTransform(Matrix3x2.Identity);
        }, EmptyField);
    }

    protected override void DisposeBehavior()
    {
        _resources.Dispose();
        base.DisposeBehavior();
    }

#if DEBUG
    internal int CacheRebuilds { get; private set; }

    public void CaptureFields(IClydeViewport viewport, Action<string, IRenderTexture> capture)
    {
        var res = _resources.GetForViewport(viewport, _ => new CachedResources(_prototypes));
        if (res.Obstacles == null)
            return;
        capture("obstacles", res.Obstacles);
        capture("seeds", res.Nearest!);
        capture("direct", res.Direct!);
        capture("indirect", res.Indirect!);
        capture("atlas", res.ActorAtlas!);
        capture("actor-shadow", res.ActorShadows!);
        capture("fill", res.Fill!);
    }
#endif

    private readonly record struct LightSource(PointLightComponent Light, Vector2 Position,
        Angle Rotation, float Height, Vector2 Axis, float Size);

    private readonly record struct Geometry(Box2 Bounds, Matrix3x2 Matrix, Color Colour);

    private readonly record struct ActorCaster(EntityUid Uid, SpriteComponent Sprite, Angle Rotation,
        Vector2 DrawPosition, float DrawScale, Vector2 Foot, float Width, float Height,
        float TextureHeight, Vector2 Anchor, Vector4 Region);

    private sealed class CachedResources : IDisposable
    {
        public readonly ShaderInstance Geometry;
        public readonly ShaderInstance Seed;
        public readonly ShaderInstance Jump;
        public readonly ShaderInstance IndirectShader;
        public readonly ShaderInstance Compose;
        public readonly ShaderInstance DirectShader;
        public readonly ShaderInstance Combine;
        public readonly ShaderInstance?[] ActorShaders = new ShaderInstance?[64];
        public IRenderTexture? Obstacles;
        public IRenderTexture? SeedsA;
        public IRenderTexture? SeedsB;
        public IRenderTexture? Direct;
        public IRenderTexture? Indirect;
        public IRenderTexture? ActorAtlas;
        public IRenderTexture? ActorShadows;
        public IRenderTexture? ActorBlur;
        public IRenderTexture? Fill;
        public IRenderTexture? SourceLight;
        public IRenderTexture? Nearest;
        public int GeometryHash;
        public Vector2 FieldOrigin;
        public Vector2 FieldSize;

        public CachedResources(IPrototypeManager prototypes)
        {
            Geometry = prototypes.Index(GeometryId).InstanceUnique();
            Seed = prototypes.Index(SeedId).InstanceUnique();
            Jump = prototypes.Index(JumpId).InstanceUnique();
            IndirectShader = prototypes.Index(IndirectId).InstanceUnique();
            Compose = prototypes.Index(ComposeId).InstanceUnique();
            DirectShader = prototypes.Index(DirectId).InstanceUnique();
            Combine = prototypes.Index(CombineId).InstanceUnique();
        }

        public void EnsureTargets(IClyde clyde, Vector2i size, Vector2i renderSize, int radianceScale)
        {
            var data = new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8);
            var light = new RenderTargetFormatParameters(RenderTargetColorFormat.R11FG11FB10F);
            var nearest = new TextureSampleParameters { Filter = false };
            var linear = new TextureSampleParameters { Filter = true };
            if (Obstacles?.Size != size)
            {
                Obstacles?.Dispose();
                SeedsA?.Dispose();
                SeedsB?.Dispose();
                Nearest = null;
                Obstacles = clyde.CreateRenderTarget(size, data, nearest, "ray-obstacles");
                SeedsA = clyde.CreateRenderTarget(size, data, nearest, "ray-seeds-a");
                SeedsB = clyde.CreateRenderTarget(size, data, nearest, "ray-seeds-b");
            }
            if (SourceLight?.Size != renderSize || Direct?.Size != renderSize * radianceScale)
            {
                SourceLight?.Dispose();
                Direct?.Dispose();
                Indirect?.Dispose();
                ActorShadows?.Dispose();
                ActorBlur?.Dispose();
                Fill?.Dispose();
                SourceLight = clyde.CreateRenderTarget(renderSize, light, linear, "ray-source");
                Direct = clyde.CreateRenderTarget(renderSize * radianceScale, light, linear, "ray-direct");
                Indirect = clyde.CreateRenderTarget(renderSize, light, linear, "ray-indirect");
                ActorShadows = clyde.CreateRenderTarget(renderSize * radianceScale, data, linear, "ray-actor-shadows");
                ActorBlur = clyde.CreateRenderTarget(renderSize * radianceScale, data, linear, "ray-actor-blur");
                Fill = clyde.CreateRenderTarget(renderSize, light, linear, "ray-fill");
            }
            ActorAtlas ??= clyde.CreateRenderTarget(new Vector2i(512, 768),
                new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8Srgb), linear, "ray-actor-atlas");
        }

        private void DisposeTargets()
        {
            Obstacles?.Dispose();
            SeedsA?.Dispose();
            SeedsB?.Dispose();
            Direct?.Dispose();
            Indirect?.Dispose();
            ActorAtlas?.Dispose();
            ActorShadows?.Dispose();
            ActorBlur?.Dispose();
            Fill?.Dispose();
            SourceLight?.Dispose();
        }

        public void Dispose()
        {
            DisposeTargets();
            Geometry.Dispose();
            Seed.Dispose();
            Jump.Dispose();
            IndirectShader.Dispose();
            Compose.Dispose();
            DirectShader.Dispose();
            Combine.Dispose();
            foreach (var shader in ActorShaders)
                shader?.Dispose();
        }
    }
}
