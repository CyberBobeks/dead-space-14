// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

namespace Content.Client.DeadSpace.WallTransparency;

[RegisterComponent]
public sealed partial class WallProximityFadeComponent : Component
{
    [DataField]
    public float FadeStartRadius = 2.0f;

    [DataField]
    public float FadeFullRadius = 1.5f;

    [DataField]
    public float MinAlpha = 0.5f;

    [DataField]
    public float FadeSpeed = 12f;

    [DataField]
    public float OcclusionHalfWidth = 1.25f;

    internal float CurrentAlpha = 1f;
}
