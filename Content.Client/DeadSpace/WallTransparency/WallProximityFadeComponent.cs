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
    public float PlayerRadiusBonus = 0.25f;

    [DataField]
    public float MinAlpha = 0.65f;

    [DataField]
    public float FadeSpeed = 20f;

    [DataField]
    public float OcclusionHalfWidth = 0.75f;

    [DataField]
    public bool UseLocalRotation;

    internal float CurrentAlpha = 1f;
}
