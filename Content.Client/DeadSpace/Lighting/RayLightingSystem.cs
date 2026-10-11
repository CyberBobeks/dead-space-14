// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

using Robust.Client.GameObjects;
using Robust.Client.Graphics;

namespace Content.Client.DeadSpace.Lighting;

public sealed class RayLightingSystem : EntitySystem
{
    public const float MaxRadius = 16f;
    public const string BlackMask = "/Textures/_DeadSpace/Lighting/black.png";

    public static bool CanRender(PointLightComponent light) => light.Radius > 0f && light.Radius <= MaxRadius;

    [Dependency] private readonly IOverlayManager _overlays = default!;
    [Dependency] private readonly PointLightSystem _lights = default!;

    private readonly Dictionary<EntityUid, PointLightComponent> _masked = new();
    private readonly HashSet<EntityUid> _seen = new();
    private readonly List<EntityUid> _removed = new();
    private RayLightingOverlay _overlay = default!;
    internal bool Enabled { get; set; } = true;

    public override void Initialize()
    {
        base.Initialize();
        _overlay = new RayLightingOverlay();
        _overlays.AddOverlay(_overlay);
    }

    public override void FrameUpdate(float frameTime)
    {
        if (!Enabled)
        {
            foreach (var (uid, light) in _masked)
            {
                if (TryComp<PointLightComponent>(uid, out var current) && current == light)
                    _lights.SetMask(light.MaskPath, light);
            }
            _masked.Clear();
            return;
        }

        _seen.Clear();
        var query = EntityQueryEnumerator<PointLightComponent>();
        while (query.MoveNext(out var uid, out var light))
        {
            if (!CanRender(light))
                continue;
            _seen.Add(uid);
            if (_masked.TryGetValue(uid, out var previous) && previous == light)
                continue;

            // The public mask API suppresses only the native light contribution. Network state,
            // bulb colours, animation and the shared FOV occluders remain untouched.
            _lights.SetMask(BlackMask, light);
            _masked[uid] = light;
        }

        _removed.Clear();
        foreach (var uid in _masked.Keys)
        {
            if (!_seen.Contains(uid))
                _removed.Add(uid);
        }
        foreach (var uid in _removed)
        {
            if (_masked.TryGetValue(uid, out var light) && TryComp<PointLightComponent>(uid, out var current) && current == light)
                _lights.SetMask(light.MaskPath, light);
            _masked.Remove(uid);
        }
    }

    public override void Shutdown()
    {
        foreach (var (uid, light) in _masked)
        {
            if (TryComp<PointLightComponent>(uid, out var current) && current == light)
                _lights.SetMask(light.MaskPath, light);
        }
        _masked.Clear();
        _overlays.RemoveOverlay(_overlay);
        _overlay.Dispose();
        base.Shutdown();
    }
}
