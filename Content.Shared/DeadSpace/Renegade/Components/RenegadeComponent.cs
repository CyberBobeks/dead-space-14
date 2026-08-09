// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.StatusIcon;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.DeadSpace.Renegade.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(SharedRenegadeSystem))]
public sealed partial class RenegadeComponent : Component
{
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public ProtoId<FactionIconPrototype> StatusIcon { get; set; } = "RenegadeFaction";

    [DataField, AutoNetworkedField]
    public Color EyeColor = new(1.0f, 1.0f, 0.0f);

    [DataField, AutoNetworkedField]
    public Color OldEyeColor = new(1.0f, 1.0f, 0.0f);

    [DataField]
    public EntProtoId ArmorPiercingStrikeAction = "ActionRenegadeArmorPiercingStrike";

    [DataField]
    public EntityUid? ArmorPiercingStrikeActionEntity;

    [DataField]
    public EntProtoId DisarmStrikeAction = "ActionRenegadeDisarmStrike";

    [DataField]
    public EntityUid? DisarmStrikeActionEntity;

    [DataField]
    public EntProtoId KnockdownStrikeAction = "ActionRenegadeKnockdownStrike";

    [DataField]
    public EntityUid? KnockdownStrikeActionEntity;

    [DataField]
    public EntProtoId FocusedRageAction = "ActionRenegadeFocusedRage";

    [DataField]
    public EntityUid? FocusedRageActionEntity;

    [DataField]
    public EntProtoId ForceChokeAction = "ActionRenegadeForceChoke";

    [DataField]
    public EntityUid? ForceChokeActionEntity;

    [DataField]
    public RenegadeSwordTechnique? SelectedTechnique;

    [DataField]
    public DamageSpecifier ArmorPiercingDamage = new()
    {
        DamageDict = new()
        {
            { "Heat", FixedPoint2.New(15) },
            { "Slash", FixedPoint2.New(15) },
        },
    };

    [DataField]
    public TimeSpan ArmorPiercingStrikeCooldown = TimeSpan.FromSeconds(6);

    [DataField]
    public TimeSpan DisarmStrikeCooldown = TimeSpan.FromSeconds(5);

    [DataField]
    public TimeSpan KnockdownStrikeCooldown = TimeSpan.FromSeconds(5);

    [DataField]
    public TimeSpan KnockdownStrikeDuration = TimeSpan.FromSeconds(3);

    [DataField]
    public TimeSpan FocusedRageDuration = TimeSpan.FromSeconds(40);

    [DataField]
    public float FocusedRageSpeedModifier = 1.15f;

    [DataField]
    public float FocusedRageEnsnareDurationMultiplier = 0.5f;

    [DataField]
    public float FocusedRageStunDurationMultiplier = 0.5f;

    [DataField]
    public float ForceChokeRange = 5f;

    [DataField]
    public TimeSpan ForceChokeDuration = TimeSpan.FromSeconds(5);

    [DataField]
    public TimeSpan ForceChokeTickInterval = TimeSpan.FromSeconds(1);

    [DataField]
    public int ForceChokeTickCount = 5;

    [DataField]
    public DamageSpecifier ForceChokeDamage = new()
    {
        DamageDict = new()
        {
            { "Asphyxiation", FixedPoint2.New(3) },
        },
    };

    [DataField]
    public float ForceChokeSpreadMultiplier = 2f;
}

public enum RenegadeSwordTechnique : byte
{
    ArmorPiercing,
    Disarm,
    Knockdown,
}
