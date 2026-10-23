using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Mpeg.Internal;

namespace Assimalign.Cohesion.Content.Mpeg;

/// <summary>The opaque hvcC payload; configuration bytes remain borrowed and are never codec-decoded.</summary>
/// <remarks>Full-box prefixes, where present, are included in Data.</remarks>
// Deviates from the repo interface-first rule per the Stage 4 brief's sealed box model.
[global::System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class HevcConfigurationBox : BmffBoxLazyLeaf, IMp4OpaqueBox
{
    /// <summary>Creates an empty configuration box.</summary>
    public HevcConfigurationBox() { }
    internal HevcConfigurationBox(BmffBoxHeader header) { }
    /// <summary>Gets or sets the complete opaque payload, excluding the box header.</summary>
    public BinarySegment Data { get => Payload; set => Payload = value; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x68766343;

    private sealed class DebugView
    {
        private readonly HevcConfigurationBox _target;

        public DebugView(HevcConfigurationBox value)
        {
            _target = value;
        }

        public BinarySegment Data => _target.Data;
        public BmffBoxType BoxType => _target.BoxType;
        public global::Assimalign.Cohesion.Content.Binary.BinarySegment Payload => _target.Payload;
        public bool IsLeaf => _target.IsLeaf;
        public bool IsComposite => _target.IsComposite;
        public long Limit => _target.Limit;
        public long Offset => _target.Offset;
        public global::Assimalign.Cohesion.Content.Bmff.BmffBoxHeader Header => _target.Header;
    }
}
