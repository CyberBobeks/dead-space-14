// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

using Content.Shared.Damage;

namespace Content.Shared.DeadSpace.Renegade.Components;

[RegisterComponent]
public sealed partial class RenegadeFocusedRageComponent : Component
{
    [DataField]
    public TimeSpan EndsAt;

    [DataField]
    public float SpeedModifier = 1.15f;

    [DataField]
    public float EnsnareDurationMultiplier = 0.5f;

    [DataField]
    public float StunDurationMultiplier = 0.5f;

    [DataField]
    public bool AppliedIgnoreSlowOnDamage;
}

[RegisterComponent]
public sealed partial class RenegadeForceChokeComponent : Component
{
    [DataField]
    public EntityUid Source;

    [DataField]
    public TimeSpan EndsAt;

    [DataField]
    public TimeSpan NextDamageTime;

    [DataField]
    public TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    [DataField]
    public int RemainingTicks;

    [DataField]
    public DamageSpecifier Damage = new();

    [DataField]
    public float SpreadMultiplier = 2f;
}
