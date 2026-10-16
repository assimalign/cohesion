using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public class BmffVisitorTests
{
    public static TheoryData<string> TypedBoxes => new()
    {
        nameof(TrackReferenceTypeBox),
        nameof(TrackFragmentDecodeTimeBox),
        nameof(DataEntryUrlBox),
        nameof(DataEntryUrnBox),
        nameof(ItemInfoEntryBox),
    };

    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - Visitors: every box dispatches to its own typed overload")]
    [MemberData(nameof(TypedBoxes))]
    public void Accept_BoxWithTypedOverload_DispatchesToThatOverload(string boxName)
    {
        // Arrange
        var box = Create(boxName);
        var generic = new NamingVisitor();
        var nongeneric = new RecordingVisitor();

        // Act
        var visited = box.Accept(generic);
        box.Accept(nongeneric);

        // Assert
        visited.ShouldBe(boxName);
        nongeneric.Visited.ShouldBe(new[] { boxName });
    }

    private static BmffBox Create(string boxName) => boxName switch
    {
        nameof(TrackReferenceTypeBox) => TrackReferenceTypeBox.Create(FourCC.Parse("hint")),
        nameof(TrackFragmentDecodeTimeBox) => new TrackFragmentDecodeTimeBox(),
        nameof(DataEntryUrlBox) => new DataEntryUrlBox(),
        nameof(DataEntryUrnBox) => new DataEntryUrnBox(),
        _ => new ItemInfoEntryBox(),
    };

    // Returns the overload's box type name; the generic fallback returns "fallback".
    private sealed class NamingVisitor : BmffBoxVisitor<string>
    {
        protected override string VisitDefault(BmffBox box) => "fallback";

        public override string Visit(TrackReferenceTypeBox box) => nameof(TrackReferenceTypeBox);

        public override string Visit(TrackFragmentDecodeTimeBox box) => nameof(TrackFragmentDecodeTimeBox);

        public override string Visit(DataEntryUrlBox box) => nameof(DataEntryUrlBox);

        public override string Visit(DataEntryUrnBox box) => nameof(DataEntryUrnBox);

        public override string Visit(ItemInfoEntryBox box) => nameof(ItemInfoEntryBox);
    }

    // Wraps the generic visitor so the nongeneric IBmffBoxVisitor entry point is exercised too.
    private sealed class RecordingVisitor : IBmffBoxVisitor
    {
        private readonly NamingVisitor _inner = new();

        public List<string> Visited { get; } = new();

        public void Visit(BmffBox box) => Visited.Add(box.Accept(_inner));

        public void Visit(TrackReferenceTypeBox box) => Visited.Add(_inner.Visit(box));

        public void Visit(TrackFragmentDecodeTimeBox box) => Visited.Add(_inner.Visit(box));

        public void Visit(DataEntryUrlBox box) => Visited.Add(_inner.Visit(box));

        public void Visit(DataEntryUrnBox box) => Visited.Add(_inner.Visit(box));

        public void Visit(ItemInfoEntryBox box) => Visited.Add(_inner.Visit(box));

        // The remaining overloads are not under test; they record the fallback.
        public void Visit(AdditionalMetaBox box) => Visited.Add("fallback");
        public void Visit(BinaryXmlBox box) => Visited.Add("fallback");
        public void Visit(ChunkOffsetBox box) => Visited.Add("fallback");
        public void Visit(ChunkOffset64BitBox box) => Visited.Add("fallback");
        public void Visit(CopyrightBox box) => Visited.Add("fallback");
        public void Visit(DataInfoBox box) => Visited.Add("fallback");
        public void Visit(DataReferenceBox box) => Visited.Add("fallback");
        public void Visit(DisposableSampleBox box) => Visited.Add("fallback");
        public void Visit(EditBox box) => Visited.Add("fallback");
        public void Visit(EditListBox box) => Visited.Add("fallback");
        public void Visit(FecReservoirBox box) => Visited.Add("fallback");
        public void Visit(FileDeliveryItemInfoBox box) => Visited.Add("fallback");
        public void Visit(FileDeliverySessionGroupBox box) => Visited.Add("fallback");
        public void Visit(FilePartitionBox box) => Visited.Add("fallback");
        public void Visit(FileTypeBox box) => Visited.Add("fallback");
        public void Visit(FreeSpaceBox box) => Visited.Add("fallback");
        public void Visit(GroupIdToNameBox box) => Visited.Add("fallback");
        public void Visit(HandlerBox box) => Visited.Add("fallback");
        public void Visit(HintMediaHeaderBox box) => Visited.Add("fallback");
        public void Visit(IpmpControlBox box) => Visited.Add("fallback");
        public void Visit(IpmpInfoBox box) => Visited.Add("fallback");
        public void Visit(ItemInfoBox box) => Visited.Add("fallback");
        public void Visit(ItemLocationBox box) => Visited.Add("fallback");
        public void Visit(ItemProtectionBox box) => Visited.Add("fallback");
        public void Visit(MediaBox box) => Visited.Add("fallback");
        public void Visit(MediaDataBox box) => Visited.Add("fallback");
        public void Visit(MediaHeaderBox box) => Visited.Add("fallback");
        public void Visit(MediaInfoBox box) => Visited.Add("fallback");
        public void Visit(MetaRelationshipBox box) => Visited.Add("fallback");
        public void Visit(MetaBox box) => Visited.Add("fallback");
        public void Visit(MovieBox box) => Visited.Add("fallback");
        public void Visit(MovieExtensionBox box) => Visited.Add("fallback");
        public void Visit(MovieExtensionHeaderBox box) => Visited.Add("fallback");
        public void Visit(MovieFragmentBox box) => Visited.Add("fallback");
        public void Visit(MovieFragmentHeaderBox box) => Visited.Add("fallback");
        public void Visit(MovieFragmentRandomAccessBox box) => Visited.Add("fallback");
        public void Visit(MovieFragmentRandomAccessOffsetBox box) => Visited.Add("fallback");
        public void Visit(MovieHeaderBox box) => Visited.Add("fallback");
        public void Visit(NullMediaHeaderBox box) => Visited.Add("fallback");
        public void Visit(OriginalFormatBox box) => Visited.Add("fallback");
        public void Visit(PartitionEntryBox box) => Visited.Add("fallback");
        public void Visit(PrimaryItemReferenceBox box) => Visited.Add("fallback");
        public void Visit(ProgressiveDownloadInfoBox box) => Visited.Add("fallback");
        public void Visit(ProtectionSchemeInfoBox box) => Visited.Add("fallback");
        public void Visit(SampleCompositionTimeBox box) => Visited.Add("fallback");
        public void Visit(SampleDecodingTimeBox box) => Visited.Add("fallback");
        public void Visit(SampleDegradationPriorityBox box) => Visited.Add("fallback");
        public void Visit(SampleDescriptionBox box) => Visited.Add("fallback");
        public void Visit(SampleGroupDescriptionBox box) => Visited.Add("fallback");
        public void Visit(SamplePaddingBitsBox box) => Visited.Add("fallback");
        public void Visit(SampleSizeBox box) => Visited.Add("fallback");
        public void Visit(CompactSampleSizeBox box) => Visited.Add("fallback");
        public void Visit(SampleSubInfoBox box) => Visited.Add("fallback");
        public void Visit(SampleTableBox box) => Visited.Add("fallback");
        public void Visit(SampleToChunkBox box) => Visited.Add("fallback");
        public void Visit(SampleToGroupBox box) => Visited.Add("fallback");
        public void Visit(SchemeInformationBox box) => Visited.Add("fallback");
        public void Visit(SchemeTypeBox box) => Visited.Add("fallback");
        public void Visit(ShadowSyncSampleTableBox box) => Visited.Add("fallback");
        public void Visit(SkipBox box) => Visited.Add("fallback");
        public void Visit(SoundMediaHeaderBox box) => Visited.Add("fallback");
        public void Visit(SyncSampleTableBox box) => Visited.Add("fallback");
        public void Visit(TrackBox box) => Visited.Add("fallback");
        public void Visit(TrackExtensionBox box) => Visited.Add("fallback");
        public void Visit(TrackFragmentBox box) => Visited.Add("fallback");
        public void Visit(TrackFragmentHeaderBox box) => Visited.Add("fallback");
        public void Visit(TrackFragmentRandomAccessBox box) => Visited.Add("fallback");
        public void Visit(TrackFragmentRunBox box) => Visited.Add("fallback");
        public void Visit(TrackHeaderBox box) => Visited.Add("fallback");
        public void Visit(TrackReferenceBox box) => Visited.Add("fallback");
        public void Visit(TrackSelectionBox box) => Visited.Add("fallback");
        public void Visit(UserDataBox box) => Visited.Add("fallback");
        public void Visit(VideoMediaHeaderBox box) => Visited.Add("fallback");
        public void Visit(XmlBox box) => Visited.Add("fallback");
        public void Visit(UnknownBox box) => Visited.Add("fallback");
    }
}
