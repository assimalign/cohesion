namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Visits each box type in the composite model.</summary>
/// <typeparam name="T">The result type.</typeparam>
public interface IBmffBoxVisitor<T>
{
    /// <summary>Visits a BmffBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(BmffBox box);
    /// <summary>Visits a AdditionalMetaBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(AdditionalMetaBox box);
    /// <summary>Visits a BinaryXmlBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(BinaryXmlBox box);
    /// <summary>Visits a ChunkOffsetBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(ChunkOffsetBox box);
    /// <summary>Visits a ChunkOffset64BitBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(ChunkOffset64BitBox box);
    /// <summary>Visits a CopyrightBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(CopyrightBox box);
    /// <summary>Visits a DataInfoBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(DataInfoBox box);
    /// <summary>Visits a DataReferenceBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(DataReferenceBox box);
    /// <summary>Visits a DisposableSampleBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(DisposableSampleBox box);
    /// <summary>Visits a EditBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(EditBox box);
    /// <summary>Visits a EditListBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(EditListBox box);
    /// <summary>Visits a FecReservoirBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(FecReservoirBox box);
    /// <summary>Visits a FileDeliveryItemInfoBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(FileDeliveryItemInfoBox box);
    /// <summary>Visits a FileDeliverySessionGroupBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(FileDeliverySessionGroupBox box);
    /// <summary>Visits a FilePartitionBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(FilePartitionBox box);
    /// <summary>Visits a FileTypeBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(FileTypeBox box);
    /// <summary>Visits a FreeSpaceBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(FreeSpaceBox box);
    /// <summary>Visits a GroupIdToNameBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(GroupIdToNameBox box);
    /// <summary>Visits a HandlerBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(HandlerBox box);
    /// <summary>Visits a HintMediaHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(HintMediaHeaderBox box);
    /// <summary>Visits a IpmpControlBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(IpmpControlBox box);
    /// <summary>Visits a IpmpInfoBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(IpmpInfoBox box);
    /// <summary>Visits a ItemInfoBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(ItemInfoBox box);
    /// <summary>Visits a ItemLocationBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(ItemLocationBox box);
    /// <summary>Visits a ItemProtectionBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(ItemProtectionBox box);
    /// <summary>Visits a MediaBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(MediaBox box);
    /// <summary>Visits a MediaDataBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(MediaDataBox box);
    /// <summary>Visits a MediaHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(MediaHeaderBox box);
    /// <summary>Visits a MediaInfoBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(MediaInfoBox box);
    /// <summary>Visits a MetaBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(MetaBox box);
    /// <summary>Visits a MetaRelationshipBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(MetaRelationshipBox box);
    /// <summary>Visits a MovieBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(MovieBox box);
    /// <summary>Visits a MovieExtensionBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(MovieExtensionBox box);
    /// <summary>Visits a MovieExtensionHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(MovieExtensionHeaderBox box);
    /// <summary>Visits a MovieFragmentBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(MovieFragmentBox box);
    /// <summary>Visits a MovieFragmentHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(MovieFragmentHeaderBox box);
    /// <summary>Visits a MovieFragmentRandomAccessBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(MovieFragmentRandomAccessBox box);
    /// <summary>Visits a MovieFragmentRandomAccessOffsetBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(MovieFragmentRandomAccessOffsetBox box);
    /// <summary>Visits a MovieHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(MovieHeaderBox box);
    /// <summary>Visits a NullMediaHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(NullMediaHeaderBox box);
    /// <summary>Visits a OriginalFormatBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(OriginalFormatBox box);
    /// <summary>Visits a PartitionEntryBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(PartitionEntryBox box);
    /// <summary>Visits a PrimaryItemReferenceBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(PrimaryItemReferenceBox box);
    /// <summary>Visits a ProgressiveDownloadInfoBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(ProgressiveDownloadInfoBox box);
    /// <summary>Visits a ProtectionSchemeInfoBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(ProtectionSchemeInfoBox box);
    /// <summary>Visits a SampleCompositionTimeBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(SampleCompositionTimeBox box);
    /// <summary>Visits a SampleDecodingTimeBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(SampleDecodingTimeBox box);
    /// <summary>Visits a SampleDegradationPriorityBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(SampleDegradationPriorityBox box);
    /// <summary>Visits a SampleDescriptionBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(SampleDescriptionBox box);
    /// <summary>Visits a SampleGroupDescriptionBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(SampleGroupDescriptionBox box);
    /// <summary>Visits a SamplePaddingBitsBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(SamplePaddingBitsBox box);
    /// <summary>Visits a SampleSizeBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(SampleSizeBox box);
    /// <summary>Visits a CompactSampleSizeBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(CompactSampleSizeBox box);
    /// <summary>Visits a SampleSubInfoBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(SampleSubInfoBox box);
    /// <summary>Visits a SampleTableBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(SampleTableBox box);
    /// <summary>Visits a SampleToChunkBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(SampleToChunkBox box);
    /// <summary>Visits a SampleToGroupBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(SampleToGroupBox box);
    /// <summary>Visits a SchemeInformationBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(SchemeInformationBox box);
    /// <summary>Visits a SchemeTypeBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(SchemeTypeBox box);
    /// <summary>Visits a ShadowSyncSampleTableBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(ShadowSyncSampleTableBox box);
    /// <summary>Visits a SkipBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(SkipBox box);
    /// <summary>Visits a SoundMediaHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(SoundMediaHeaderBox box);
    /// <summary>Visits a SyncSampleTableBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(SyncSampleTableBox box);
    /// <summary>Visits a TrackBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(TrackBox box);
    /// <summary>Visits a TrackExtensionBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(TrackExtensionBox box);
    /// <summary>Visits a TrackFragmentBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(TrackFragmentBox box);
    /// <summary>Visits a TrackFragmentHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(TrackFragmentHeaderBox box);
    /// <summary>Visits a TrackFragmentRandomAccessBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(TrackFragmentRandomAccessBox box);
    /// <summary>Visits a TrackFragmentRunBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(TrackFragmentRunBox box);
    /// <summary>Visits a TrackHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(TrackHeaderBox box);
    /// <summary>Visits a TrackReferenceBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(TrackReferenceBox box);
    /// <summary>Visits a TrackSelectionBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(TrackSelectionBox box);
    /// <summary>Visits a UserDataBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(UserDataBox box);
    /// <summary>Visits a VideoMediaHeaderBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(VideoMediaHeaderBox box);
    /// <summary>Visits a XmlBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(XmlBox box);
    /// <summary>Visits a UnknownBox.</summary>
    /// <param name="box">The box to visit.</param>
    /// <returns>The visit result.</returns>
    T Visit(UnknownBox box);
}
