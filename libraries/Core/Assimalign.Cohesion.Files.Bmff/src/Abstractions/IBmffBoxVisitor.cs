﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Files.Bmff;

public interface IBmffBoxVisitor
{
    void Visit(BmffBox box);
    void Visit(AdditionalMetaBox box);
    void Visit(BinaryXmlBox box);
    void Visit(ChunkOffsetBox box);
    void Visit(ChunkOffset64BitBox box);
    void Visit(CopyrightBox box);
    void Visit(DataInfoBox box);
    void Visit(DataReferenceBox box);
    void Visit(DisposableSampleBox box);
    void Visit(EditBox box);
    void Visit(EditListBox box);
    void Visit(FecReservoirBox box);
    void Visit(FileDeliveryItemInfoBox box);
    void Visit(FileDeliverySessionGroupBox box);
    void Visit(FilePartitionBox box);
    void Visit(FileTypeBox box);
    void Visit(FreeSpaceBox box);
    void Visit(GroupIdToNameBox box);
    void Visit(HandlerBox box);
    void Visit(HintMediaHeaderBox box);
    void Visit(IpmpControlBox box);
    void Visit(IpmpInfoBox box);
    void Visit(ItemLocationBox box);
    void Visit(ItemProtectionBox box);
    void Visit(MediaBox box);
    void Visit(MediaDataBox box);
    void Visit(MediaHeaderBox box);
    void Visit(MediaInfoBox box);
    void Visit(MetaRelationshipBox box);
    void Visit(MetaBox box);
    void Visit(MovieBox box);
    void Visit(MovieExtensionBox box);
    void Visit(MovieExtensionHeaderBox box);
    void Visit(MovieFragmentBox box);
    void Visit(MovieFragmentHeaderBox box);
    void Visit(MovieFragmentRandomAccessBox box);
    void Visit(MovieFragmentRandomAccessOffsetBox box);
    void Visit(MovieHeaderBox box);
    void Visit(NullMediaHeaderBox box);
    void Visit(OriginalFormatBox box);
    void Visit(PartitionEntryBox box);
    void Visit(PrimaryItemReferenceBox box);
    void Visit(ProgressiveDownloadInfoBox box);
    void Visit(ProtectionSchemeInfoBox box);
    void Visit(SampleCompositionTimeBox box);
    void Visit(SampleDecodingTimeBox box);
    void Visit(SampleDegradationPriorityBox box);
    void Visit(SampleDescriptionBox box);
    void Visit(SampleGroupDescriptionBox box);
    void Visit(SamplePaddingBitsBox box);
    void Visit(SampleSizeBox box);
    void Visit(SampleSubInfoBox box);
    void Visit(SampleTableBox box);
    void Visit(SampleToChunkBox box);
    void Visit(SampleToGroupBox box);
    void Visit(SchemeInformationBox box);
    void Visit(SchemeTypeBox box);
    void Visit(ShadowSyncSampleTableBox box);
    void Visit(SkipBox box);
    void Visit(SoundMediaHeaderBox box);
    void Visit(SyncSampleTableBox box);
    void Visit(TrackBox box);
    void Visit(TrackExtensionBox box);
    void Visit(TrackFragmentBox box);
    void Visit(TrackFragmentHeaderBox box);
    void Visit(TrackFragmentRandomAccessBox box);
    void Visit(TrackFragmentRunBox box);
    void Visit(TrackHeaderBox box);
    void Visit(TrackSelectionBox box);
    void Visit(UserDataBox box);
    void Visit(VideoMediaHeaderBox box);
    void Visit(XmlBox box);

}
