namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Visits each box type in the composite model.</summary>
public interface IBmffBoxVisitor
{
    /// <summary>Visits a BmffBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(BmffBox box);
    /// <summary>Visits a AdditionalMetaBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(AdditionalMetaBox box);
    /// <summary>Visits a BinaryXmlBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(BinaryXmlBox box);
    /// <summary>Visits a ChunkOffsetBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(ChunkOffsetBox box);
    /// <summary>Visits a ChunkOffset64BitBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(ChunkOffset64BitBox box);
    /// <summary>Visits a CopyrightBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(CopyrightBox box);
    /// <summary>Visits a DataInfoBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(DataInfoBox box);
    /// <summary>Visits a DataReferenceBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(DataReferenceBox box);
    /// <summary>Visits a DisposableSampleBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(DisposableSampleBox box);
    /// <summary>Visits a EditBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(EditBox box);
    /// <summary>Visits a EditListBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(EditListBox box);
    /// <summary>Visits a FecReservoirBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(FecReservoirBox box);
    /// <summary>Visits a FileDeliveryItemInfoBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(FileDeliveryItemInfoBox box);
    /// <summary>Visits a FileDeliverySessionGroupBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(FileDeliverySessionGroupBox box);
    /// <summary>Visits a FilePartitionBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(FilePartitionBox box);
    /// <summary>Visits a FileTypeBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(FileTypeBox box);
    /// <summary>Visits a FreeSpaceBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(FreeSpaceBox box);
    /// <summary>Visits a GroupIdToNameBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(GroupIdToNameBox box);
    /// <summary>Visits a HandlerBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(HandlerBox box);
    /// <summary>Visits a HintMediaHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(HintMediaHeaderBox box);
    /// <summary>Visits a IpmpControlBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(IpmpControlBox box);
    /// <summary>Visits a IpmpInfoBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(IpmpInfoBox box);
    /// <summary>Visits a ItemInfoBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(ItemInfoBox box);
    /// <summary>Visits a ItemLocationBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(ItemLocationBox box);
    /// <summary>Visits a ItemProtectionBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(ItemProtectionBox box);
    /// <summary>Visits a MediaBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(MediaBox box);
    /// <summary>Visits a MediaDataBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(MediaDataBox box);
    /// <summary>Visits a MediaHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(MediaHeaderBox box);
    /// <summary>Visits a MediaInfoBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(MediaInfoBox box);
    /// <summary>Visits a MetaBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(MetaBox box);
    /// <summary>Visits a MetaRelationshipBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(MetaRelationshipBox box);
    /// <summary>Visits a MovieBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(MovieBox box);
    /// <summary>Visits a MovieExtensionBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(MovieExtensionBox box);
    /// <summary>Visits a MovieExtensionHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(MovieExtensionHeaderBox box);
    /// <summary>Visits a MovieFragmentBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(MovieFragmentBox box);
    /// <summary>Visits a MovieFragmentHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(MovieFragmentHeaderBox box);
    /// <summary>Visits a MovieFragmentRandomAccessBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(MovieFragmentRandomAccessBox box);
    /// <summary>Visits a MovieFragmentRandomAccessOffsetBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(MovieFragmentRandomAccessOffsetBox box);
    /// <summary>Visits a MovieHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(MovieHeaderBox box);
    /// <summary>Visits a NullMediaHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(NullMediaHeaderBox box);
    /// <summary>Visits a OriginalFormatBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(OriginalFormatBox box);
    /// <summary>Visits a PartitionEntryBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(PartitionEntryBox box);
    /// <summary>Visits a PrimaryItemReferenceBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(PrimaryItemReferenceBox box);
    /// <summary>Visits a ProgressiveDownloadInfoBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(ProgressiveDownloadInfoBox box);
    /// <summary>Visits a ProtectionSchemeInfoBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(ProtectionSchemeInfoBox box);
    /// <summary>Visits a SampleCompositionTimeBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(SampleCompositionTimeBox box);
    /// <summary>Visits a SampleDecodingTimeBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(SampleDecodingTimeBox box);
    /// <summary>Visits a SampleDegradationPriorityBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(SampleDegradationPriorityBox box);
    /// <summary>Visits a SampleDescriptionBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(SampleDescriptionBox box);
    /// <summary>Visits a SampleGroupDescriptionBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(SampleGroupDescriptionBox box);
    /// <summary>Visits a SamplePaddingBitsBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(SamplePaddingBitsBox box);
    /// <summary>Visits a SampleSizeBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(SampleSizeBox box);
    /// <summary>Visits a CompactSampleSizeBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(CompactSampleSizeBox box);
    /// <summary>Visits a SampleSubInfoBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(SampleSubInfoBox box);
    /// <summary>Visits a SampleTableBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(SampleTableBox box);
    /// <summary>Visits a SampleToChunkBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(SampleToChunkBox box);
    /// <summary>Visits a SampleToGroupBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(SampleToGroupBox box);
    /// <summary>Visits a SchemeInformationBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(SchemeInformationBox box);
    /// <summary>Visits a SchemeTypeBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(SchemeTypeBox box);
    /// <summary>Visits a ShadowSyncSampleTableBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(ShadowSyncSampleTableBox box);
    /// <summary>Visits a SkipBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(SkipBox box);
    /// <summary>Visits a SoundMediaHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(SoundMediaHeaderBox box);
    /// <summary>Visits a SyncSampleTableBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(SyncSampleTableBox box);
    /// <summary>Visits a TrackBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(TrackBox box);
    /// <summary>Visits a TrackExtensionBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(TrackExtensionBox box);
    /// <summary>Visits a TrackFragmentBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(TrackFragmentBox box);
    /// <summary>Visits a TrackFragmentHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(TrackFragmentHeaderBox box);
    /// <summary>Visits a TrackFragmentRandomAccessBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(TrackFragmentRandomAccessBox box);
    /// <summary>Visits a TrackFragmentRunBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(TrackFragmentRunBox box);
    /// <summary>Visits a TrackHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(TrackHeaderBox box);
    /// <summary>Visits a TrackReferenceBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(TrackReferenceBox box);
    /// <summary>Visits a TrackReferenceTypeBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(TrackReferenceTypeBox box);
    /// <summary>Visits a TrackFragmentDecodeTimeBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(TrackFragmentDecodeTimeBox box);
    /// <summary>Visits a DataEntryUrlBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(DataEntryUrlBox box);
    /// <summary>Visits a DataEntryUrnBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(DataEntryUrnBox box);
    /// <summary>Visits an ItemInfoEntryBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(ItemInfoEntryBox box);
    /// <summary>Visits a TrackSelectionBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(TrackSelectionBox box);
    /// <summary>Visits a UserDataBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(UserDataBox box);
    /// <summary>Visits a VideoMediaHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(VideoMediaHeaderBox box);
    /// <summary>Visits a XmlBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(XmlBox box);
    /// <summary>Visits a UnknownBox.</summary>
    /// <param name="box">The box to visit.</param>
    void Visit(UnknownBox box);
}
