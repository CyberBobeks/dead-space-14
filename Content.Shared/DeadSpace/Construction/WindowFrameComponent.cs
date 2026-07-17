// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

using Content.Shared.Construction.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared.DeadSpace.Construction;

[RegisterComponent]
public sealed partial class WindowFrameComponent : Component
{
    [DataField(required: true)]
    public List<ProtoId<ConstructionPrototype>> Options = [];
}
