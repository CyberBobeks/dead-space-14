// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

#if DEBUG
using Stopwatch = System.Diagnostics.Stopwatch;
using System.IO;
using System.Numerics;
using Content.Shared.Administration;
using Robust.Client;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Console;
using Robust.Shared.ContentPack;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Color = Robust.Shared.Maths.Color;

namespace Content.Client.DeadSpace.Lighting;

[AnyCommand]
public sealed class RayLightingRenderCommand : IConsoleCommand
{
    private static readonly EntProtoId Wall = "WallSolid";
    private static readonly EntProtoId Human = "MobHuman";
    private static readonly EntProtoId Crate = "CrateGenericSteel";
    private static readonly EntProtoId WarmLight = "AlwaysPoweredWallLight";
    private static readonly EntProtoId BlueLight = "AlwaysPoweredlightCyan";
    private static readonly ResPath Output = new("/Screenshots/DS14Lighting");

    [Dependency] private readonly IClyde _clyde = default!;
    [Dependency] private readonly IBaseClient _client = default!;
    [Dependency] private readonly IOverlayManager _overlays = default!;
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IGameController _game = default!;
    [Dependency] private readonly IMapManager _maps = default!;
    [Dependency] private readonly IResourceManager _resources = default!;
    [Dependency] private readonly ITileDefinitionManager _tiles = default!;
    [Dependency] private readonly IUserInterfaceManager _ui = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private IClydeViewport? _viewport;
    private IConsoleShell? _shell;
    private string _mode = "ray";
    private int _frames;
    private bool _capturing;
    private int _pending;
    private readonly List<double> _renderTimes = new();
    private readonly List<double> _frameTimes = new();
    private EntityUid _grid;
    private EntityUid _human;
    private EntityUid _warmLight;
    private EntityUid _crate;

    public string Command => "ds14_light_render";
    public string Description => "Render a deterministic lighting scene and quit.";
    public string Help => "ds14_light_render <baseline|ray|moved|light-moved|geometry-moved|rotated|consoles|consoles-baseline|consoles-rotated|stress|stress-baseline|walking|walking-camera>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (_viewport != null || args.Length != 1 || args[0] is not ("baseline" or "ray" or "moved" or "light-moved" or "geometry-moved" or "rotated" or "consoles" or "consoles-baseline" or "consoles-rotated" or "stress" or "stress-baseline" or "walking" or "walking-camera"))
        {
            shell.WriteError(Help);
            return;
        }
        _shell = shell;
        _mode = args[0];
        _client.StartSinglePlayer();
        _entities.System<RayLightingSystem>().Enabled = !_mode.Contains("baseline");
        var mapSystem = _entities.System<SharedMapSystem>();
        mapSystem.CreateMap(out var mapId, runMapInit: true);
        mapSystem.SetAmbientLight(mapId, Color.Black);
        var grid = _maps.CreateGridEntity(mapId);
        _grid = grid.Owner;
        var floor = new Tile(_tiles["FloorSteel"].TileId);
        var tiles = new List<(Vector2i, Tile)>();
        for (var x = -8; x <= 7; x++)
        for (var y = -5; y <= 5; y++)
            tiles.Add((new Vector2i(x, y), floor));
        mapSystem.SetTiles(grid.Owner, grid.Comp, tiles);
        for (var x = -8; x <= 7; x++)
        {
            _entities.SpawnEntity(Wall, new EntityCoordinates(grid.Owner, new Vector2(x + 0.5f, -4.5f)));
            _entities.SpawnEntity(Wall, new EntityCoordinates(grid.Owner, new Vector2(x + 0.5f, 5.5f)));
        }
        for (var y = -4; y <= 4; y++)
        {
            _entities.SpawnEntity(Wall, new EntityCoordinates(grid.Owner, new Vector2(-7.5f, y + 0.5f)));
            _entities.SpawnEntity(Wall, new EntityCoordinates(grid.Owner, new Vector2(7.5f, y + 0.5f)));
        }
        for (var y = 0; y <= 4; y++)
            _entities.SpawnEntity(Wall, new EntityCoordinates(grid.Owner, new Vector2(1.5f, y + 0.5f)));

        _crate = _entities.SpawnEntity(Crate, new EntityCoordinates(grid.Owner, new Vector2(-1.5f, -0.5f)));
        _entities.SpawnEntity(Crate, new EntityCoordinates(grid.Owner, new Vector2(4.5f, 0.5f)));
        _human = _entities.SpawnEntity(Human, new EntityCoordinates(grid.Owner, new Vector2(-3.5f, 0.5f)));
        if (!_mode.StartsWith("consoles"))
        {
            _warmLight = _entities.SpawnEntity(WarmLight, new EntityCoordinates(grid.Owner, new Vector2(-3.5f, 4.5f)));
            _entities.SpawnEntity(BlueLight, new EntityCoordinates(grid.Owner, new Vector2(4.5f, 4.5f)));
        }

        if (_mode.StartsWith("consoles") || _mode.StartsWith("stress"))
        {
            var transforms = _entities.System<SharedTransformSystem>();
            var lights = _entities.System<PointLightSystem>();
            for (var index = 0; index < 4; index++)
            {
                var console = _entities.SpawnEntity("ComputerAlert",
                    new EntityCoordinates(grid.Owner, new Vector2(-5.5f + index * 3f, -2.5f)));
                transforms.SetLocalRotationNoLerp(console, Angle.FromDegrees(index * 90));
                lights.SetEnabled(console, true);
            }
        }
        if (_mode.StartsWith("stress"))
        {
            for (var x = -6; x <= 6; x += 3)
            for (var y = -3; y <= 3; y += 3)
                _entities.SpawnEntity(WarmLight, new EntityCoordinates(grid.Owner, new Vector2(x + 0.5f, y + 0.5f)));
        }
        if (_mode.StartsWith("walking"))
        {
            _entities.SpawnEntity(WarmLight, new EntityCoordinates(grid.Owner, new Vector2(-6.5f, 2.5f)));
            _entities.SpawnEntity(WarmLight, new EntityCoordinates(grid.Owner, new Vector2(-0.5f, 2.5f)));
        }

        _viewport = _clyde.CreateViewport(new Vector2i(960, 640), "ray-lighting-fixture");
        _viewport.AutomaticRender = false;
        _viewport.Eye = new FixedEye
        {
            Position = new MapCoordinates(new Vector2(0f, 0.5f), mapId),
            Zoom = new Vector2(0.5f),
            DrawLight = true,
            DrawFov = false,
            Rotation = _mode.EndsWith("rotated") ? Angle.FromDegrees(90) : Angle.Zero,
        };
        _ui.OnPostDrawUIRoot += OnDraw;
        shell.WriteLine($"Lighting fixture: {_mode}");
    }

    private void OnDraw(PostDrawUIRootEventArgs args)
    {
        if (_viewport == null || _capturing)
            return;
        if (_mode.StartsWith("walking") && _frames >= 60)
        {
            var phase = (_frames - 60) / 180f;
            var position = new Vector2(-5.5f + phase * 4f, 0.5f + 0.25f * MathF.Sin(phase * MathF.Tau));
            var transforms = _entities.System<SharedTransformSystem>();
            transforms.SetCoordinates(_human, new EntityCoordinates(_grid, position));
            transforms.SetLocalRotationNoLerp(_human, Angle.FromDegrees(90));
            if (_mode == "walking-camera" && _viewport.Eye is FixedEye eye)
                eye.Position = new MapCoordinates(new Vector2(position.X + 3.5f, 0.5f), eye.Position.MapId);
        }
        if (_frames == 120)
        {
            var transforms = _entities.System<SharedTransformSystem>();
            if (_mode == "moved")
                transforms.SetCoordinates(_human, new EntityCoordinates(_grid, new Vector2(-3.5f, -1.5f)));
            else if (_mode == "light-moved")
                transforms.SetCoordinates(_warmLight, new EntityCoordinates(_grid, new Vector2(-5.5f, 4.5f)));
            else if (_mode == "geometry-moved")
                transforms.SetCoordinates(_crate, new EntityCoordinates(_grid, new Vector2(-0.5f, -1.5f)));
        }
        _entities.System<RayLightingSystem>().FrameUpdate(0f);
        var start = Stopwatch.GetTimestamp();
        _viewport.Render();
        if (_mode.StartsWith("walking") && _frames >= 60 && _frames % 12 == 0)
        {
            var frame = _frames;
            _pending++;
            _viewport.RenderTarget.CopyPixelsToMemory<Rgba32>(image =>
            {
                SaveField(image, $"frame-{frame:D3}");
            });
        }
        if (_frames >= 60)
        {
            _renderTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            _frameTimes.Add(_timing.RealFrameTime.TotalMilliseconds);
        }
        if (++_frames < 240)
            return;
        _renderTimes.Sort();
        _frameTimes.Sort();
        _shell?.WriteLine($"DS14_LIGHT_TIMING {_mode} render_ms p50={_renderTimes[_renderTimes.Count / 2]:F2} p95={_renderTimes[(int) (_renderTimes.Count * 0.95)]:F2}; frame_ms p50={_frameTimes[_frameTimes.Count / 2]:F2} p95={_frameTimes[(int) (_frameTimes.Count * 0.95)]:F2}");
        _shell?.WriteLine($"DS14_LIGHT_CACHE_REBUILDS {_overlays.GetOverlay<RayLightingOverlay>().CacheRebuilds}");
        _capturing = true;
        _ui.OnPostDrawUIRoot -= OnDraw;
        _pending++;
        if (!_mode.Contains("baseline"))
        {
            _overlays.GetOverlay<RayLightingOverlay>().CaptureFields(_viewport,
                (name, target) =>
                {
                    _pending++;
                    target.CopyPixelsToMemory<Rgba32>(image => SaveField(image, name));
                });
        }
        _viewport.RenderTarget.CopyPixelsToMemory<Rgba32>(Save);
    }

    private void Save(Image<Rgba32> image)
    {
        try
        {
            _resources.UserData.CreateDir(Output);
            var path = Output / $"{_mode}.png";
            using var stream = _resources.UserData.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
            image.SaveAsPng(stream);
            _shell?.WriteLine($"DS14_LIGHT_RENDER_OK {path}");
        }
        finally
        {
            image.Dispose();
            FinishCapture();
        }
    }

    private void SaveField(Image<Rgba32> image, string name)
    {
        try
        {
            _resources.UserData.CreateDir(Output);
            using var stream = _resources.UserData.Open(Output / $"{_mode}-{name}.png", FileMode.Create, FileAccess.Write, FileShare.None);
            image.SaveAsPng(stream);
        }
        finally
        {
            image.Dispose();
            FinishCapture();
        }
    }

    private void FinishCapture()
    {
        if (--_pending != 0 || !_capturing)
            return;
        _viewport?.Dispose();
        _viewport = null;
        _game.Shutdown("Lighting render complete");
    }
}
#endif
