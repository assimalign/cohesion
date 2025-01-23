using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Files.Bmff.Internal;

internal sealed partial class BmffReaderDefault
{
    private readonly static ConcurrentDictionary<BmffBoxType, Func<long, long, BmffBox>> boxes = new();

    static BmffReaderDefault()
    {
        boxes.GetOrAdd(BmffBoxType.AdditionalMeta, (offset, limit) => new AdditionalMetaBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.BinaryXml, (offset, limit) => new BinaryXmlBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.ChunkOffset, (offset, limit) => new ChunkOffsetBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.ChunkOffset6fBit, (offset, limit) => new ChunkOffset64BitBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.Copyright, (offset, limit) => new CopyrightBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.DataInfo, (offset, limit) => new DataInfoBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.DataReference, (offset, limit) => new DataReferenceBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.DisposableSample, (offset, limit) => new DisposableSampleBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.Edit, (offset, limit) => new EditBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.EditList, (offset, limit) => new EditListBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.FecReservoir, (offset, limit) => new FecReservoirBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.FileDeliveryItemInfo, (offset, limit) => new FileDeliveryItemInfoBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.FileDeliverySessionGroup, (offset, limit) => new FileDeliverySessionGroupBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.FilePartition, (offset, limit) => new FilePartitionBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.FileType, (offset, limit) => new FileTypeBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.FreeSpace, (offset, limit) => new FreeSpaceBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.GroupIdToName, (offset, limit) => new GroupIdToNameBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.Handler, (offset, limit) => new HandlerBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.HintMediaHeader, (offset, limit) => new HintMediaHeaderBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.IpmpControl, (offset, limit) => new IpmpControlBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.IpmpInfo, (offset, limit) => new IpmpInfoBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.ItemInfo, (offset, limit) => new ItemInfoBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.ItemLocation, (offset, limit) => new ItemLocationBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.ItemProtection, (offset, limit) => new ItemProtectionBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.Media, (offset, limit) => new MediaBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.MediaData, (offset, limit) => new MediaDataBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.MediaHeader, (offset, limit) => new MediaHeaderBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.MediaInfo, (offset, limit) => new MediaInfoBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.Meta, (offset, limit) => new MetaBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.MetaRelationship, (offset, limit)=> new MetaRelationshipBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.Movie, (offset, limit) => new MovieBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.MovieExtension, (offset, limit) => new MovieExtensionBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.MovieExtensionHeader, (offset, limit) => new MovieExtensionHeaderBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.MovieFragment, (offset, limit) => new MovieFragmentBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.MovieFragmentHeader, (offset, limit) => new MovieFragmentHeaderBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.MovieFragmentRandomAccess, (offset, limit) => new MovieFragmentRandomAccessBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.MovieFragmentRandomAccessOffset, (offset, limit) => new MovieFragmentRandomAccessOffsetBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.MovieHeader, (offset, limit) => new MovieHeaderBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.NullMediaHeader, (offset, limit) => new NullMediaHeaderBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.OriginalFormat, (offset, limit) => new OriginalFormatBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.PartitionEntry, (offset, limit) => new PartitionEntryBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.PrimaryItemReference, (offset, limit) => new PrimaryItemReferenceBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.ProgressiveDownloadInfo, (offset, limit) => new ProgressiveDownloadInfoBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.ProgressiveDownloadInfo, (offset, limit) => new ProgressiveDownloadInfoBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.ProtectionSchemeInfo, (offset, limit) => new ProtectionSchemeInfoBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.SampleCompositionTime, (offset, limit) => new SampleCompositionTimeBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.SampleDecodingTime, (offset, limit) => new SampleDescriptionBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.SampleDegradationPriority, (offset, limit) => new SampleDegradationPriorityBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.SampleDescription, (offset, limit) => new SampleDescriptionBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.SampleGroupDescription, (offset, limit) => new SampleGroupDescriptionBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.SamplePaddingBits, (offset, limit) => new SamplePaddingBitsBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.SampleSize, (offset, limit) => new SampleSizeBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.CompactSampleSize, (offset, limit) => new CompactSampleSizeBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.SubSampleInformation, (offset, limit) => new SampleSubInfoBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.SampleTable, (offset, limit) => new SampleTableBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.SampleToChunk, (offset, limit) => new SampleToChunkBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.SampleToGroup, (offset, limit) => new SampleToGroupBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.SchemeInformation, (offset, limit) => new SchemeInformationBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.SchemeType, (offset, limit) => new SchemeTypeBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.ShadowSyncSampleTable, (offset, limit) => new ShadowSyncSampleTableBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.Skip, (offset, limit) => new SkipBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.SoundMediaHeader, (offset, limit) => new SoundMediaHeaderBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.SyncSampleTable, (offset, limit) => new SyncSampleTableBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.Track, (offset, limit) => new TrackBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.TrackExtension, (offset, limit) => new TrackExtensionBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.TrackFragment, (offset, limit) => new TrackFragmentBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.TrackFragmentHeader, (offset, limit) => new TrackFragmentHeaderBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.TrackFragmentRandomAccess, (offset, limit) => new TrackFragmentRandomAccessBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.TrackFragmentRun, (offset, limit) => new TrackFragmentRunBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.TrackHeader, (offset, limit) => new TrackHeaderBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.TrackReference, (offset, limit) => new TrackReferenceBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.TrackSelection, (offset, limit) => new TrackSelectionBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.UserData, (offset, limit) => new UserDataBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.VideoMediaHeader, (offset, limit) => new VideoMediaHeaderBox(offset, limit));
        boxes.GetOrAdd(BmffBoxType.Xml, (offset, limit) => new XmlBox(offset, limit));
    }
}
