using JetBrains.Annotations;
using Robust.Client.Graphics;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype;
using Robust.Shared.Utility;

namespace Content.Client.IconSmoothing
{
    /// <summary>
    ///     Makes sprites of other grid-aligned entities like us connect.
    /// </summary>
    /// <remarks>
    ///     The system is based on Baystation12's smoothwalling, and thus will work with those.
    ///     To use, set <c>base</c> equal to the prefix of the corner states in the sprite base RSI.
    ///     Any objects with the same <c>key</c> will connect.
    /// </remarks>
    [RegisterComponent]
    public sealed partial class IconSmoothComponent : Component
    {
        [ViewVariables(VVAccess.ReadWrite), DataField("enabled")]
        public bool Enabled = true;

        public (EntityUid?, Vector2i)? LastPosition;

        /// <summary>
        ///     We will smooth with other objects with the same key.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite), DataField("key")]
        public string? SmoothKey { get; private set; }

        /// <summary>
        ///     Additional keys to smooth with.
        /// </summary>
        [DataField]
        public List<string> AdditionalKeys = new();

        // DS14-start
        /// <summary>
        ///     Other smoothing keys may connect to this entity when they start with one of these prefixes.
        /// </summary>
        [DataField]
        public List<string> MatchingKeyPrefixes = new();
        // DS14-end

        /// <summary>
        ///     Prepended to the RSI state.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite), DataField("base")]
        public string StateBase { get; set; } = string.Empty;

        // DS14-start
        /// <summary>
        ///     Extra corner layer sets that follow the same smoothing result as the primary corner layers.
        /// </summary>
        [DataField]
        public Dictionary<string, IconSmoothAdditionalCornerLayer> AdditionalCornerLayers = new();
        // DS14-end

        [DataField("shader", customTypeSerializer:typeof(PrototypeIdSerializer<ShaderPrototype>))]
        public string? Shader;

        /// <summary>
        ///     Mode that controls how the icon should be selected.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite), DataField("mode")]
        public IconSmoothingMode Mode = IconSmoothingMode.Corners;

        /// <summary>
        ///     Used by <see cref="IconSmoothSystem"/> to reduce redundant updates.
        /// </summary>
        internal int UpdateGeneration { get; set; }
    }

    // DS14-start
    [DataDefinition]
    public sealed partial class IconSmoothAdditionalCornerLayer
    {
        [DataField("base", required: true)]
        public string StateBase = string.Empty;

        [DataField]
        public ResPath? Sprite;

        internal int SouthEastLayer = -1;
        internal int NorthEastLayer = -1;
        internal int NorthWestLayer = -1;
        internal int SouthWestLayer = -1;

        private string? _cachedStateBase;
        private RSI.StateId[]? _cachedStates;

        internal RSI.StateId GetState(int cornerFill)
        {
            if (_cachedStates == null || _cachedStateBase != StateBase)
            {
                _cachedStateBase = StateBase;
                _cachedStates =
                [
                    $"{StateBase}0",
                    $"{StateBase}1",
                    $"{StateBase}2",
                    $"{StateBase}3",
                    $"{StateBase}4",
                    $"{StateBase}5",
                    $"{StateBase}6",
                    $"{StateBase}7",
                ];
            }

            return _cachedStates[cornerFill];
        }
    }
    // DS14-end

    /// <summary>
    ///     Controls the mode with which icon smoothing is calculated.
    /// </summary>
    [PublicAPI]
    public enum IconSmoothingMode : byte
    {
        /// <summary>
        ///     Each icon is made up of 4 corners, each of which can get a different state depending on
        ///     adjacent entities clockwise, counter-clockwise and diagonal with the corner.
        /// </summary>
        Corners,

        /// <summary>
        ///     There are 16 icons, only one of which is used at once.
        ///     The icon selected is a bit field made up of the cardinal direction flags that have adjacent entities.
        /// </summary>
        CardinalFlags,

        /// <summary>
        ///     The icon represents a triangular sprite with only 2 states, representing South / East being occupied or not.
        /// </summary>
        Diagonal,

        /// <summary>
        ///     Where this component contributes to our neighbors being calculated but we do not update our own sprite.
        /// </summary>
        NoSprite,
    }

}
