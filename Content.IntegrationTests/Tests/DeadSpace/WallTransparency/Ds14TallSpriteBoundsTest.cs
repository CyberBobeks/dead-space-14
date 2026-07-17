// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

using System.Linq;
using System.Numerics;
using Content.Server.Doors.Systems;
using Content.Shared.Doors.Components;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;
using DrawDepth = Content.Shared.DrawDepth.DrawDepth;

namespace Content.IntegrationTests.Tests.DeadSpace.WallTransparency;

[TestFixture]
public sealed class Ds14TallSpriteBoundsTest
{
    [Test]
    public async Task TallWallWindowAndAllAirlockSpritesCalculateClientBounds()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var server = pair.Server;
        var client = pair.Client;

        var serverEntManager = server.ResolveDependency<IEntityManager>();
        var clientEntManager = client.ResolveDependency<IEntityManager>();
        var doorSystem = serverEntManager.System<DoorSystem>();
        var transformSystem = serverEntManager.System<SharedTransformSystem>();
        var spriteQuery = clientEntManager.GetEntityQuery<SpriteComponent>();
        var spriteSystem = clientEntManager.System<SpriteSystem>();
        var prototypeManager = server.ResolveDependency<IPrototypeManager>();

        var testMap = await pair.CreateTestMap();

        var prototypes = prototypeManager.EnumeratePrototypes<EntityPrototype>()
            .Where(proto => !proto.Abstract)
            .Where(proto => IsAirlockPrototype(prototypeManager, proto))
            .Select(proto => proto.ID)
            .Concat([
            "WallSolid",
            "WallReinforced",
            "WallSolidDiagonal",
            "Window",
            "TintedWindow",
            "ReinforcedWindow",
            "PlasmaWindow",
            "ReinforcedPlasmaWindow",
            "UraniumWindow",
            "ReinforcedUraniumWindow",
            "ShuttleWindow",
            "MiningWindow",
            "ClockworkWindow",
            "PlastitaniumWindow",
            "PlastitaniumPlasmaWindow",
            "XenoResinWindow",
            "XenoborgWindow",
            ])
            .Distinct()
            .Order()
            .ToArray();

        var serverEntities = new EntityUid[prototypes.Length];

        await server.WaitPost(() =>
        {
            for (var i = 0; i < prototypes.Length; i++)
            {
                serverEntities[i] = serverEntManager.SpawnEntity(prototypes[i], testMap.GridCoords);
            }
        });

        await pair.RunTicksSync(5);

        await AssertClientBounds(
            client,
            clientEntManager,
            serverEntManager,
            spriteQuery,
            spriteSystem,
            serverEntities,
            prototypes,
            "spawned");

        var states = new[]
        {
            DoorState.Open,
            DoorState.Closed,
            DoorState.Opening,
            DoorState.Closing,
            DoorState.Denying,
        };

        var rotations = new[]
        {
            Angle.Zero,
            Angle.FromDegrees(90),
            Angle.FromDegrees(180),
            Angle.FromDegrees(270),
        };

        foreach (var rotation in rotations)
        {
            foreach (var state in states)
            {
                await server.WaitPost(() =>
                {
                    foreach (var serverEnt in serverEntities)
                    {
                        if (!serverEntManager.HasComponent<DoorComponent>(serverEnt))
                            continue;

                        transformSystem.SetLocalRotation(serverEnt, rotation);
                        doorSystem.SetState(serverEnt, state);
                    }
                });

                await pair.RunTicksSync(2);
                await AssertClientBounds(
                    client,
                    clientEntManager,
                    serverEntManager,
                    spriteQuery,
                    spriteSystem,
                    serverEntities,
                    prototypes,
                    $"{state}, {rotation}");
            }
        }

        await server.WaitPost(() =>
        {
            foreach (var serverEnt in serverEntities)
            {
                serverEntManager.DeleteEntity(serverEnt);
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AllAirlockDescendantsUseTheSharedTallVisual()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var server = pair.Server;
        var client = pair.Client;
        var serverPrototypes = server.ResolveDependency<IPrototypeManager>();
        var clientPrototypes = client.ResolveDependency<IPrototypeManager>();
        var serverComponentFactory = server.ResolveDependency<IComponentFactory>();
        var clientComponentFactory = client.ResolveDependency<IComponentFactory>();
        var expectedDepth = (int) DrawDepth.Mobs;
        const string expectedRsi = "/Textures/_DeadSpace/Structures/Doors/Airlocks/standard.rsi";

        var prototypes = serverPrototypes.EnumeratePrototypes<EntityPrototype>()
            .Where(proto => !proto.Abstract)
            .Where(proto => IsAirlockPrototype(serverPrototypes, proto))
            .OrderBy(proto => proto.ID)
            .ToArray();

        Assert.That(prototypes, Is.Not.Empty);

        await client.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                foreach (var serverPrototype in prototypes)
                {
                    var clientPrototype = clientPrototypes.Index<EntityPrototype>(serverPrototype.ID);

                    Assert.That(
                        clientPrototype.TryGetComponent<SpriteComponent>(out var sprite, clientComponentFactory),
                        Is.True,
                        $"{serverPrototype.ID} has no Sprite component.");
                    Assert.That(
                        sprite.BaseRSI?.Path.ToString(),
                        Is.EqualTo(expectedRsi),
                        $"{serverPrototype.ID} uses a departmental RSI.");
                    Assert.That(sprite.Offset, Is.EqualTo(Vector2.Zero), $"{serverPrototype.ID} has a sprite offset.");
                    Assert.That(sprite.SnapCardinals, Is.False, $"{serverPrototype.ID} snaps to cardinals.");
                    Assert.That(sprite.DrawDepth, Is.EqualTo(expectedDepth), $"{serverPrototype.ID} has wrong draw depth.");

                    Assert.That(
                        serverPrototype.TryGetComponent<DoorComponent>(out var door, serverComponentFactory),
                        Is.True,
                        $"{serverPrototype.ID} has no Door component.");
                    Assert.That(
                        door.OpenDrawDepth,
                        Is.EqualTo(expectedDepth),
                        $"{serverPrototype.ID} has wrong open draw depth.");
                    Assert.That(
                        door.ClosedDrawDepth,
                        Is.EqualTo(expectedDepth),
                        $"{serverPrototype.ID} has wrong closed draw depth.");
                }
            });
        });

        await pair.CleanReturnAsync();
    }

    private static async Task AssertClientBounds(
        RobustIntegrationTest.ClientIntegrationInstance client,
        IEntityManager clientEntManager,
        IEntityManager serverEntManager,
        EntityQuery<SpriteComponent> spriteQuery,
        SpriteSystem spriteSystem,
        EntityUid[] serverEntities,
        string[] prototypes,
        string context)
    {
        await client.WaitAssertion(() =>
        {
            for (var i = 0; i < serverEntities.Length; i++)
            {
                var clientEnt = clientEntManager.GetEntity(serverEntManager.GetNetEntity(serverEntities[i]));
                var sprite = spriteQuery.GetComponent(clientEnt);

                try
                {
                    _ = spriteSystem.GetLocalBounds((clientEnt, sprite));
                }
                catch (Exception ex)
                {
                    Assert.Fail($"{prototypes[i]} failed sprite bounds in {context}: {ex}");
                }
            }
        });
    }

    private static bool IsAirlockPrototype(IPrototypeManager prototypeManager, EntityPrototype prototype)
    {
        if (prototype.ID is "Airlock" or "HighSecDoor")
            return true;

        return prototypeManager.EnumerateParents<EntityPrototype>(prototype.ID)?
            .Any(parent => parent.ID is "Airlock" or "HighSecDoor") == true;
    }
}
