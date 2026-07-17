// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

using Content.Server.Construction;
using Content.Server.Construction.Components;
using Content.Shared.Construction;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Database;
using Content.Shared.DeadSpace.Construction;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Content.Shared.Whitelist;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.DeadSpace.Construction;

public sealed class WindowFrameSystem : EntitySystem
{
    [Dependency] private readonly ConstructionSystem _construction = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<WindowFrameComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<WindowFrameComponent, GetVerbsEvent<Verb>>(OnGetVerbs);
        SubscribeLocalEvent<WindowFrameComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<WindowFrameComponent, InteractUsingEvent>(
            OnInteractUsing,
            before: [typeof(ConstructionSystem)]);
    }

    private void OnStartup(Entity<WindowFrameComponent> ent, ref ComponentStartup args)
    {
        if (!TryComp<ConstructionComponent>(ent, out var construction))
            return;

        EnsureDefaultSelection(ent, construction);
    }

    private void OnGetVerbs(Entity<WindowFrameComponent> ent, ref GetVerbsEvent<Verb> args)
    {
        if (!args.CanAccess ||
            !args.CanInteract ||
            !args.CanComplexInteract ||
            !TryComp<ConstructionComponent>(ent, out var construction) ||
            construction.EdgeIndex != null)
            return;

        EnsureDefaultSelection(ent, construction);
        var user = args.User;

        foreach (var optionId in ent.Comp.Options)
        {
            if (!TryGetOption(ent, user, optionId, construction, out var option, out var targetPrototype))
                continue;

            var targetNode = option.TargetNode;
            var targetName = targetPrototype.Name;
            var verb = new Verb
            {
                Priority = ent.Comp.Options.Count - ent.Comp.Options.IndexOf(optionId),
                Category = VerbCategory.SelectType,
                Text = targetName,
                Icon = new SpriteSpecifier.EntityPrototype(targetPrototype.ID),
                Disabled = construction.TargetNode == targetNode,
                Impact = LogImpact.Low,
                DoContactInteraction = true,
                Act = () =>
                {
                    if (!_construction.SetPathfindingTarget(ent, targetNode, construction))
                        return;

                    _popup.PopupEntity(
                        Loc.GetString("window-frame-selected", ("type", targetName)),
                        ent,
                        user);
                },
            };

            args.Verbs.Add(verb);
        }
    }

    private void OnExamined(Entity<WindowFrameComponent> ent, ref ExaminedEvent args)
    {
        if (!TryComp<ConstructionComponent>(ent, out var construction))
            return;

        EnsureDefaultSelection(ent, construction);

        foreach (var optionId in ent.Comp.Options)
        {
            if (!_prototype.TryIndex(optionId, out ConstructionPrototype? option) ||
                option.TargetNode != construction.TargetNode ||
                !TryGetTargetPrototype(ent, option, out var targetPrototype))
                continue;

            args.PushMarkup(Loc.GetString("window-frame-current", ("type", targetPrototype.Name)));
            return;
        }
    }

    private void OnInteractUsing(Entity<WindowFrameComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled ||
            !TryComp<ConstructionComponent>(ent, out var construction))
            return;

        EnsureDefaultSelection(ent, construction);

        foreach (var optionId in ent.Comp.Options)
        {
            if (!_prototype.TryIndex(optionId, out ConstructionPrototype? option) ||
                option.TargetNode != construction.TargetNode ||
                !_whitelist.IsWhitelistFail(option.EntityWhitelist, args.User))
                continue;

            args.Handled = true;
            _popup.PopupEntity(Loc.GetString("window-frame-restricted"), ent, args.User);
            return;
        }
    }

    private bool TryGetOption(
        EntityUid frame,
        EntityUid user,
        ProtoId<ConstructionPrototype> optionId,
        ConstructionComponent construction,
        out ConstructionPrototype option,
        out EntityPrototype targetPrototype)
    {
        option = default!;
        targetPrototype = default!;

        if (!_prototype.TryIndex(optionId, out ConstructionPrototype? indexedOption))
            return false;

        option = indexedOption;
        return option.Graph == construction.Graph &&
               option.StartNode == construction.Node &&
               !_whitelist.IsWhitelistFail(option.EntityWhitelist, user) &&
               TryGetTargetPrototype(frame, option, out targetPrototype);
    }

    private bool TryGetTargetPrototype(
        EntityUid frame,
        ConstructionPrototype option,
        out EntityPrototype targetPrototype)
    {
        targetPrototype = default!;

        if (!_prototype.TryIndex(option.Graph, out ConstructionGraphPrototype? graph) ||
            !graph.Nodes.TryGetValue(option.TargetNode, out var targetNode) ||
            targetNode.Entity.GetId(frame, null, new(EntityManager)) is not { } targetPrototypeId)
            return false;

        if (!_prototype.TryIndex(targetPrototypeId, out EntityPrototype? indexedPrototype))
            return false;

        targetPrototype = indexedPrototype;
        return true;
    }

    private void EnsureDefaultSelection(Entity<WindowFrameComponent> ent, ConstructionComponent construction)
    {
        if (construction.TargetNode != null ||
            ent.Comp.Options.Count == 0 ||
            !_prototype.TryIndex(ent.Comp.Options[0], out ConstructionPrototype? option))
            return;

        _construction.SetPathfindingTarget(ent, option.TargetNode, construction);
    }
}
