// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

using System.Collections.Generic;
using System.Linq;
using Content.Server.Atmos.Components;
using Content.Server.Construction.Completions;
using Content.Server.Construction.Components;
using Content.Shared.Construction;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Construction.Steps;
using Content.Shared.DeadSpace.Construction;
using Content.Shared.Prototypes;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.DeadSpace.Construction;

[TestFixture]
public sealed class Ds14WindowConstructionTest
{
    private static readonly Dictionary<string, (string Material, int Amount)[]> Materials = new()
    {
        ["window"] = [("Glass", 2)],
        ["reinforcedWindow"] = [("ReinforcedGlass", 2)],
        ["tintedWindow"] = [("ReinforcedGlass", 2)],
        ["miningWindow"] = [("ReinforcedGlass", 2)],
        ["plasmaWindow"] = [("PlasmaGlass", 2)],
        ["reinforcedPlasmaWindow"] = [("ReinforcedPlasmaGlass", 2)],
        ["uraniumWindow"] = [("UraniumGlass", 2)],
        ["reinforcedUraniumWindow"] = [("ReinforcedUraniumGlass", 2)],
        ["clockworkWindow"] = [("ClockworkGlass", 2)],
        ["shuttleWindow"] = [("Plasteel", 2), ("ReinforcedGlass", 2)],
        ["plastitaniumWindow"] = [("Plastitanium", 2), ("ReinforcedGlass", 2)],
        ["plastitaniumplasmaWindow"] = [("Plastitanium", 2), ("ReinforcedPlasmaGlass", 2)],
    };

    private static readonly Dictionary<string, string[]> Refunds = new()
    {
        ["window"] = ["SheetGlass1"],
        ["reinforcedWindow"] = ["SheetRGlass1"],
        ["tintedWindow"] = ["SheetRGlass1"],
        ["miningWindow"] = ["SheetRGlass1"],
        ["plasmaWindow"] = ["SheetPGlass1"],
        ["reinforcedPlasmaWindow"] = ["SheetRPGlass1"],
        ["uraniumWindow"] = ["SheetUGlass1"],
        ["reinforcedUraniumWindow"] = ["SheetRUGlass1"],
        ["clockworkWindow"] = ["SheetClockworkGlass1"],
        ["shuttleWindow"] = ["SheetRGlass1", "SheetPlasteel1"],
        ["plastitaniumWindow"] = ["SheetRGlass1", "SheetPlastitanium1"],
        ["plastitaniumplasmaWindow"] = ["SheetRPGlass1", "SheetPlastitanium1"],
    };

    private static readonly Dictionary<string, string> Entities = new()
    {
        ["window"] = "Window",
        ["reinforcedWindow"] = "ReinforcedWindow",
        ["tintedWindow"] = "TintedWindow",
        ["miningWindow"] = "MiningWindow",
        ["plasmaWindow"] = "PlasmaWindow",
        ["reinforcedPlasmaWindow"] = "ReinforcedPlasmaWindow",
        ["uraniumWindow"] = "UraniumWindow",
        ["reinforcedUraniumWindow"] = "ReinforcedUraniumWindow",
        ["clockworkWindow"] = "ClockworkWindow",
        ["shuttleWindow"] = "ShuttleWindow",
        ["plastitaniumWindow"] = "PlastitaniumWindow",
        ["plastitaniumplasmaWindow"] = "PlastitaniumPlasmaWindow",
    };

    private static readonly string[] ConstructionOptions =
    [
        "Window",
        "ReinforcedWindow",
        "TintedWindow",
        "MiningWindow",
        "PlasmaWindow",
        "ReinforcedPlasmaWindow",
        "UraniumWindow",
        "ReinforcedUraniumWindow",
        "ClockworkWindow",
        "ShuttleWindow",
        "PlastitaniumWindow",
        "PlastitaniumPlasmaWindow",
    ];

    private static readonly string[] UnchangedWindowRecipes =
    [
        "ClockworkWindowDiagonal",
        "MiningWindowDiagonal",
        "PlasmaReinforcedWindowDirectional",
        "PlasmaWindowDiagonal",
        "PlasmaWindowDirectional",
        "PlastitaniumPlasmaWindowDiagonal",
        "PlastitaniumWindowDiagonal",
        "ReinforcedPlasmaWindowDiagonal",
        "ReinforcedUraniumWindowDiagonal",
        "ReinforcedWindowDiagonal",
        "ShuttleWindowDiagonal",
        "UraniumReinforcedWindowDirectional",
        "UraniumWindowDiagonal",
        "UraniumWindowDirectional",
        "WindowClockworkDirectional",
        "WindowDiagonal",
        "WindowDirectional",
        "WindowReinforcedDirectional",
    ];

    [Test]
    public async Task UniversalFrameConnectsEveryFullTileWindowAndPreservesOtherRecipes()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var prototypeManager = server.ResolveDependency<IPrototypeManager>();
        var componentFactory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var graph = prototypeManager.Index<ConstructionGraphPrototype>("Window");
            var frame = graph.Nodes["frame"];

            Assert.Multiple(() =>
            {
                Assert.That(graph.Nodes["start"].GetEdge("frame"), Is.Not.Null);
                Assert.That(
                    frame.Entity.GetId(null, null, new(server.EntMan)),
                    Is.EqualTo("WindowFrame"));
                Assert.That(
                    frame.Edges.Select(edge => edge.Target),
                    Is.EquivalentTo(Materials.Keys.Append("start")));

                foreach (var (nodeId, expectedMaterials) in Materials)
                {
                    var edge = frame.GetEdge(nodeId);
                    Assert.That(edge, Is.Not.Null, $"Frame has no edge to {nodeId}.");

                    var actualMaterials = edge!.Steps
                        .OfType<MaterialConstructionGraphStep>()
                        .Select(step => (step.MaterialPrototypeId.Id, step.Amount))
                        .ToArray();
                    Assert.That(actualMaterials, Is.EqualTo(expectedMaterials), $"Wrong materials for {nodeId}.");

                    var node = graph.Nodes[nodeId];
                    Assert.That(
                        node.Entity.GetId(null, null, new(server.EntMan)),
                        Is.EqualTo(Entities[nodeId]),
                        $"Wrong entity for {nodeId}.");
                    Assert.That(node.Edges, Has.Count.EqualTo(1), $"{nodeId} should only deconstruct to the frame.");
                    Assert.That(node.Edges[0].Target, Is.EqualTo("frame"), $"{nodeId} does not leave a frame.");

                    var actualRefunds = node.Edges[0].Completed
                        .OfType<GivePrototype>()
                        .Select(action => action.Prototype.Id)
                        .ToArray();
                    Assert.That(actualRefunds, Is.EqualTo(Refunds[nodeId]), $"Wrong refunds for {nodeId}.");
                    Assert.That(
                        node.Edges[0].Completed.OfType<GivePrototype>().All(action => action.Amount == 2),
                        Is.True,
                        $"Wrong refund amount for {nodeId}.");
                }

                var frameRemoval = frame.GetEdge("start");
                Assert.That(frameRemoval, Is.Not.Null);
                var rodRefund = frameRemoval!.Completed.OfType<GivePrototype>().Single();
                Assert.That(rodRefund.Prototype.Id, Is.EqualTo("PartRodMetal1"));
                Assert.That(rodRefund.Amount, Is.EqualTo(2));
                Assert.That(frameRemoval.Completed.OfType<DeleteEntity>().Count(), Is.EqualTo(1));
            });

            var frameEntity = prototypeManager.Index<EntityPrototype>("WindowFrame");
            Assert.Multiple(() =>
            {
                Assert.That(
                    frameEntity.TryGetComponent<WindowFrameComponent>(out var frameComponent, componentFactory),
                    Is.True);
                Assert.That(
                    frameComponent.Options.Select(option => option.Id),
                    Is.EqualTo(ConstructionOptions));
                Assert.That(frameEntity.HasComponent<AirtightComponent>(componentFactory), Is.False);
                Assert.That(frameEntity.HasComponent<OccluderComponent>(componentFactory), Is.False);

                Assert.That(
                    frameEntity.TryGetComponent<ConstructionComponent>(out var construction, componentFactory),
                    Is.True);
                Assert.That(construction.Graph, Is.EqualTo("Window"));
                Assert.That(construction.Node, Is.EqualTo("frame"));
                Assert.That(construction.DeconstructionNode, Is.EqualTo("start"));
            });

            Assert.Multiple(() =>
            {
                var visibleFrameRecipe = prototypeManager.Index<ConstructionPrototype>("WindowFrame");
                Assert.That(visibleFrameRecipe.Hide, Is.False);
                Assert.That(visibleFrameRecipe.StartNode, Is.EqualTo("start"));
                Assert.That(visibleFrameRecipe.TargetNode, Is.EqualTo("frame"));

                foreach (var recipeId in ConstructionOptions)
                {
                    var recipe = prototypeManager.Index<ConstructionPrototype>(recipeId);
                    Assert.That(recipe.Graph.Id, Is.EqualTo("Window"), $"{recipeId} uses an old graph.");
                    Assert.That(recipe.StartNode, Is.EqualTo("frame"), $"{recipeId} does not start at the frame.");
                    Assert.That(recipe.Hide, Is.True, $"{recipeId} is still visible in the construction menu.");
                }

                foreach (var recipeId in new[] { "PlastitaniumWindow", "PlastitaniumPlasmaWindow" })
                {
                    var recipe = prototypeManager.Index<ConstructionPrototype>(recipeId);
                    Assert.That(recipe.EntityWhitelist?.Tags?.Select(tag => tag.Id), Does.Contain("TaipanRole"));
                }

                foreach (var recipeId in UnchangedWindowRecipes)
                {
                    var recipe = prototypeManager.Index<ConstructionPrototype>(recipeId);
                    Assert.That(recipe.StartNode, Is.EqualTo("start"), $"{recipeId} was converted to the frame graph.");
                    Assert.That(recipe.Hide, Is.False, $"{recipeId} was hidden.");
                }
            });

            foreach (var (nodeId, entityId) in Entities)
            {
                var entity = prototypeManager.Index<EntityPrototype>(entityId);
                Assert.That(
                    entity.TryGetComponent<ConstructionComponent>(out var construction, componentFactory),
                    Is.True,
                    $"{entityId} has no Construction component.");
                Assert.That(construction.Graph, Is.EqualTo("Window"), $"{entityId} uses an old graph.");
                Assert.That(construction.Node, Is.EqualTo(nodeId), $"{entityId} uses the wrong node.");
                Assert.That(construction.DeconstructionNode, Is.EqualTo("frame"), $"{entityId} does not stop at the frame.");
            }

            foreach (var legacyGraphId in new[] { "Windows", "PlastitaniumWindow" })
            {
                var legacyGraph = prototypeManager.Index<ConstructionGraphPrototype>(legacyGraphId);
                Assert.That(legacyGraph.Nodes.TryGetValue("frame", out var legacyFrame), Is.True);
                Assert.That(
                    legacyFrame!.Entity.GetId(null, null, new(server.EntMan)),
                    Is.EqualTo("WindowFrame"));
            }
        });

        await pair.CleanReturnAsync();
    }
}
