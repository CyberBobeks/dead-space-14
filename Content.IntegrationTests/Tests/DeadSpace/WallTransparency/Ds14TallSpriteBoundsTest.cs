// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

using Content.Server.Doors.Systems;
using Content.Shared.Doors.Components;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests.DeadSpace.WallTransparency;

[TestFixture]
public sealed class Ds14TallSpriteBoundsTest
{
    [Test]
    public async Task TallWallWindowAndServiceAirlockSpritesCalculateClientBounds()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var server = pair.Server;
        var client = pair.Client;

        var serverEntManager = server.ResolveDependency<IEntityManager>();
        var clientEntManager = client.ResolveDependency<IEntityManager>();
        var doorSystem = serverEntManager.System<DoorSystem>();
        var spriteQuery = clientEntManager.GetEntityQuery<SpriteComponent>();
        var spriteSystem = clientEntManager.System<SpriteSystem>();

        var testMap = await pair.CreateTestMap();

        var prototypes = new[]
        {
            "AirlockServiceLocked",
            "AirlockTheatreLocked",
            "AirlockServiceTheatreLocked",
            "AirlockChapelLocked",
            "AirlockJanitorLocked",
            "AirlockKitchenLocked",
            "AirlockBarLocked",
            "AirlockBarKitchenLocked",
            "AirlockMaintServiceLocked",
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
        };

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

        foreach (var state in states)
        {
            await server.WaitPost(() =>
            {
                foreach (var serverEnt in serverEntities)
                {
                    if (!serverEntManager.HasComponent<DoorComponent>(serverEnt))
                        continue;

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
                state.ToString());
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
}
