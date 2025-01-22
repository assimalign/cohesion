﻿namespace Assimalign.Cohesion.Files.Bmff;

public interface IBmffBoxVisitor<T>
{
    T Visit(BmffBox box);
    T Visit(AdditionalMetaBox box);
    T Visit(BinaryXmlBox box);
    T Visit(ChunkOffsetBox box);
    T Visit(ChunkOffset64BitBox box);
    T Visit(CopyrightBox box);
    T Visit(DataInfoBox box);
    T Visit(DataReferenceBox box);
    T Visit(DisposableSampleBox box);
    T Visit(EditBox box);
    T Visit(EditListBox box);
    T Visit(FecReservoirBox box);
    T Visit(FileDeliveryItemInfoBox box);
    T Visit(FileDeliverySessionGroupBox box);
    T Visit(FilePartitionBox box);
    T Visit(FileTypeBox box);
    T Visit(FreeSpaceBox box);
    T Visit(GroupIdToNameBox box);
    T Visit(HandlerBox box);
    T Visit(HintMediaHeaderBox box);
    T Visit(IpmpControlBox box);
    T Visit(IpmpInfoBox box);
    T Visit(ItemLocationBox box);
    T Visit(ItemProtectionBox box);
    T Visit(MediaBox box);
    T Visit(MediaDataBox box);
    T Visit(MediaHeaderBox box);
    T Visit(MediaInfoBox box);
    T Visit(MetaRelationshipBox box);
    T Visit(MetaBox box);
    T Visit(MovieBox box);
    T Visit(MovieExtensionBox box);
    T Visit(MovieExtensionHeaderBox box);
    T Visit(MovieFragmentBox box);
    T Visit(MovieFragmentHeaderBox box);
    T Visit(MovieFragmentRandomAccessBox box);
    T Visit(MovieFragmentRandomAccessOffsetBox box);
    T Visit(MovieHeaderBox box);
    T Visit(NullMediaHeaderBox box);
    T Visit(OriginalFormatBox box);
    T Visit(PartitionEntryBox box);
    T Visit(PrimaryItemReferenceBox box);
    T Visit(ProgressiveDownloadInfoBox box);
    T Visit(ProtectionSchemeInfoBox box);
    T Visit(SampleCompositionTimeBox box);
    T Visit(SampleDecodingTimeBox box);
    T Visit(SampleDegradationPriorityBox box);
    T Visit(SampleDescriptionBox box);
    T Visit(SampleGroupDescriptionBox box);
    T Visit(SamplePaddingBitsBox box);
    T Visit(SampleSizeBox box);
    T Visit(SampleSubInfoBox box);
    T Visit(SampleTableBox box);
    T Visit(SampleToChunkBox box);
    T Visit(SampleToGroupBox box);
    T Visit(SchemeInformationBox box);
    T Visit(SchemeTypeBox box);
    T Visit(ShadowSyncSampleTableBox box);
    T Visit(SkipBox box);
    T Visit(SoundMediaHeaderBox box);
    T Visit(SyncSampleTableBox box);
    T Visit(TrackBox box);
    T Visit(TrackExtensionBox box);
    T Visit(TrackFragmentBox box);
    T Visit(TrackFragmentHeaderBox box);
    T Visit(TrackFragmentRandomAccessBox box);
    T Visit(TrackFragmentRunBox box);
    T Visit(TrackHeaderBox box);
    T Visit(TrackSelectionBox box);
    T Visit(UserDataBox box);
    T Visit(VideoMediaHeaderBox box);
    T Visit(XmlBox box);
}
