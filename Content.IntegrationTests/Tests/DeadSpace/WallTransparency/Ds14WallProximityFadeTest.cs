// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

using System.Numerics;
using System.Linq;
using Content.Client.DeadSpace.WallTransparency;
using Robust.Client.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests.DeadSpace.WallTransparency;

[TestFixture]
public sealed class Ds14WallProximityFadeTest
{
    [TestPrototypes]
    private const string Prototypes = """
- type: entity
  id: Ds14WallFadeItem
  components:
  - type: Item
  - type: Sprite

- type: entity
  id: Ds14WallFadeStaticItem
  components:
  - type: Item
  - type: Sprite
  - type: Physics
    bodyType: Static
""";

    [Test]
    public async Task PlayerAndWorldItemsFadeWallsButContainedHiddenAndNonItemsDoNot()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var server = pair.Server;
        var client = pair.Client;

        var serverEntManager = server.ResolveDependency<IEntityManager>();
        var clientEntManager = client.ResolveDependency<IEntityManager>();
        var serverPlayers = server.ResolveDependency<IPlayerManager>();
        var serverTransform = serverEntManager.System<SharedTransformSystem>();
        var containerSystem = serverEntManager.System<SharedContainerSystem>();
        var clientSpriteSystem = clientEntManager.System<SpriteSystem>();
        var spriteQuery = clientEntManager.GetEntityQuery<SpriteComponent>();
        var fadeQuery = clientEntManager.GetEntityQuery<WallProximityFadeComponent>();
        var testMap = await pair.CreateTestMap();

        EntityUid wall = default;
        EntityUid player = default;
        EntityUid item = default;
        EntityUid holder = default;
        BaseContainer container = default!;

        await server.WaitPost(() =>
        {
            wall = serverEntManager.SpawnEntity("WallSolid", testMap.GridCoords);
            // Full wall sprites stay visually upright while their four-directional
            // RSI state follows map rotation. Fade direction must ignore this rotation.
            serverTransform.SetLocalRotation(wall, Angle.FromDegrees(90f));
            player = serverEntManager.SpawnEntity(
                "MobHuman",
                testMap.GridCoords.Offset(new Vector2(5f, 5f)));
            server.PlayerMan.SetAttachedEntity(serverPlayers.Sessions.Single(), player);
        });

        await pair.RunTicksSync(10);

        var clientWall = clientEntManager.GetEntity(serverEntManager.GetNetEntity(wall));
        await client.WaitAssertion(() =>
        {
            Assert.That(fadeQuery.GetComponent(clientWall).PlayerRadiusBonus, Is.EqualTo(0.25f));
        });

        // Player-only bonus: 2.02 is outside the item fade radius, but inside the player's 2.25 radius.
        await server.WaitPost(() =>
        {
            serverTransform.SetCoordinates(
                player,
                testMap.GridCoords.Offset(new Vector2(0f, 2.02f)));
        });
        await pair.RunTicksSync(40);
        Assert.That(await GetWallAlpha(client, clientWall, spriteQuery), Is.InRange(0.70f, 0.90f));

        await server.WaitPost(() =>
        {
            serverTransform.SetCoordinates(
                player,
                testMap.GridCoords.Offset(new Vector2(5f, 5f)));
            item = serverEntManager.SpawnEntity(
                "Ds14WallFadeItem",
                testMap.GridCoords.Offset(new Vector2(0f, 2.02f)));
        });
        await pair.RunTicksSync(40);
        Assert.That(await GetWallAlpha(client, clientWall, spriteQuery), Is.GreaterThan(0.98f));

        // A visible item directly in the world fades the wall.
        await server.WaitPost(() =>
        {
            serverTransform.SetCoordinates(
                item,
                testMap.GridCoords.Offset(new Vector2(0f, 1f)));
        });
        await pair.RunTicksSync(40);
        Assert.That(await GetWallAlpha(client, clientWall, spriteQuery), Is.InRange(0.64f, 0.70f));

        // Items beside the wall do not reveal it even inside the lateral width.
        await server.WaitPost(() =>
        {
            serverTransform.SetCoordinates(
                item,
                testMap.GridCoords.Offset(new Vector2(0.5f, 0.1f)));
        });
        await pair.RunTicksSync(40);
        Assert.That(await GetWallAlpha(client, clientWall, spriteQuery), Is.GreaterThan(0.98f));

        // Static ItemComponent structures are not pickupable sources.
        await server.WaitPost(() =>
        {
            serverEntManager.DeleteEntity(item);
            item = serverEntManager.SpawnEntity(
                "Ds14WallFadeStaticItem",
                testMap.GridCoords.Offset(new Vector2(0f, 1f)));
        });
        await pair.RunTicksSync(40);
        Assert.That(await GetWallAlpha(client, clientWall, spriteQuery), Is.GreaterThan(0.98f));

        // The same item stops being a source while in a container.
        await server.WaitPost(() =>
        {
            serverEntManager.DeleteEntity(item);
            item = serverEntManager.SpawnEntity(
                "Ds14WallFadeItem",
                testMap.GridCoords.Offset(new Vector2(0f, 1f)));
            holder = serverEntManager.SpawnEntity(
                null,
                testMap.GridCoords.Offset(new Vector2(0f, 1f)));
            container = containerSystem.EnsureContainer<Container>(holder, "fade-test");
            Assert.That(containerSystem.Insert(item, container, force: true));
        });
        await pair.RunTicksSync(40);
        Assert.That(await GetWallAlpha(client, clientWall, spriteQuery), Is.GreaterThan(0.98f));

        // Hidden item sprites are also ignored.
        await server.WaitPost(() =>
        {
            Assert.That(containerSystem.Remove(item, container, reparent: true, force: true));
            serverTransform.SetCoordinates(
                item,
                testMap.GridCoords.Offset(new Vector2(0f, 1f)));
        });
        await pair.RunTicksSync(5);

        var clientItem = clientEntManager.GetEntity(serverEntManager.GetNetEntity(item));
        await client.WaitPost(() =>
        {
            var sprite = spriteQuery.GetComponent(clientItem);
            clientSpriteSystem.SetVisible((clientItem, sprite), false);
        });
        await pair.RunTicksSync(40);
        Assert.That(await GetWallAlpha(client, clientWall, spriteQuery), Is.GreaterThan(0.98f));

        // Airlocks are structures, not ItemComponent sources.
        await server.WaitPost(() =>
        {
            serverEntManager.DeleteEntity(item);
            serverEntManager.SpawnEntity(
                "Airlock",
                testMap.GridCoords.Offset(new Vector2(0f, 1f)));
        });
        await pair.RunTicksSync(40);
        Assert.That(await GetWallAlpha(client, clientWall, spriteQuery), Is.GreaterThan(0.98f));

        // With multiple sources the strongest fade wins, then alpha recovers when it disappears.
        await server.WaitPost(() =>
        {
            serverTransform.SetCoordinates(
                player,
                testMap.GridCoords.Offset(new Vector2(0f, 2f)));
            item = serverEntManager.SpawnEntity(
                "Ds14WallFadeItem",
                testMap.GridCoords.Offset(new Vector2(0f, 1f)));
        });
        await pair.RunTicksSync(40);
        Assert.That(await GetWallAlpha(client, clientWall, spriteQuery), Is.InRange(0.64f, 0.70f));

        await server.WaitPost(() => serverEntManager.DeleteEntity(item));
        await pair.RunTicksSync(40);
        Assert.That(await GetWallAlpha(client, clientWall, spriteQuery), Is.InRange(0.80f, 0.86f));

        await server.WaitPost(() =>
        {
            serverTransform.SetCoordinates(
                player,
                testMap.GridCoords.Offset(new Vector2(5f, 5f)));
        });
        await pair.RunTicksSync(40);
        Assert.That(await GetWallAlpha(client, clientWall, spriteQuery), Is.GreaterThan(0.98f));

        await pair.CleanReturnAsync();
    }

    private static async Task<float> GetWallAlpha(
        RobustIntegrationTest.ClientIntegrationInstance client,
        EntityUid wall,
        EntityQuery<SpriteComponent> spriteQuery)
    {
        var alpha = 0f;
        await client.WaitAssertion(() => alpha = spriteQuery.GetComponent(wall).Color.A);
        return alpha;
    }
}
