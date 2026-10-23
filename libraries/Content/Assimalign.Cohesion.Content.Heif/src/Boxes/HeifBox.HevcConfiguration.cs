using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The hvcC leaf with an opaque, borrowed payload; no codec parsing.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
[global::System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class HevcConfigurationBox : BmffBoxLazyLeaf
{
    /// <summary>Creates an empty HevcConfigurationBox model.</summary>
    public HevcConfigurationBox() { }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x68766343;
    internal HeifReaderOptions Options { get; set; } = new();

    private sealed class DebugView
    {
        private readonly HevcConfigurationBox _target;

        public DebugView(HevcConfigurationBox value)
        {
            _target = value;
        }

        public BmffBoxType BoxType => _target.BoxType;
        public global::Assimalign.Cohesion.Content.Binary.BinarySegment Payload => _target.Payload;
        public bool IsLeaf => _target.IsLeaf;
        public bool IsComposite => _target.IsComposite;
        public long Limit => _target.Limit;
        public long Offset => _target.Offset;
        public global::Assimalign.Cohesion.Content.Bmff.BmffBoxHeader Header => _target.Header;
    }
}
