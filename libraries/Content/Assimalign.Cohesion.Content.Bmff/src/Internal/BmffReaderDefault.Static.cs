using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Files.Bmff.Internal;

internal sealed partial class BmffReaderDefault
{
    private readonly static ConcurrentDictionary<BmffBoxType, Func<long, long, BmffBox>> _boxes = new();

    static BmffReaderDefault()
    {
        _boxes.GetOrAdd(BmffBoxType.AdditionalMeta, (offset, limit) => new AdditionalMetaBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.BinaryXml, (offset, limit) => new BinaryXmlBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.ChunkOffset, (offset, limit) => new ChunkOffsetBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.ChunkOffset6fBit, (offset, limit) => new ChunkOffset64BitBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.Copyright, (offset, limit) => new CopyrightBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.DataInfo, (offset, limit) => new DataInfoBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.DataReference, (offset, limit) => new DataReferenceBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.DisposableSample, (offset, limit) => new DisposableSampleBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.Edit, (offset, limit) => new EditBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.EditList, (offset, limit) => new EditListBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.FecReservoir, (offset, limit) => new FecReservoirBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.FileDeliveryItemInfo, (offset, limit) => new FileDeliveryItemInfoBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.FileDeliverySessionGroup, (offset, limit) => new FileDeliverySessionGroupBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.FilePartition, (offset, limit) => new FilePartitionBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.FileType, (offset, limit) => new FileTypeBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.FreeSpace, (offset, limit) => new FreeSpaceBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.GroupIdToName, (offset, limit) => new GroupIdToNameBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.Handler, (offset, limit) => new HandlerBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.HintMediaHeader, (offset, limit) => new HintMediaHeaderBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.IpmpControl, (offset, limit) => new IpmpControlBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.IpmpInfo, (offset, limit) => new IpmpInfoBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.ItemInfo, (offset, limit) => new ItemInfoBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.ItemLocation, (offset, limit) => new ItemLocationBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.ItemProtection, (offset, limit) => new ItemProtectionBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.Media, (offset, limit) => new MediaBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.MediaData, (offset, limit) => new MediaDataBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.MediaHeader, (offset, limit) => new MediaHeaderBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.MediaInfo, (offset, limit) => new MediaInfoBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.Meta, (offset, limit) => new MetaBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.MetaRelationship, (offset, limit)=> new MetaRelationshipBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.Movie, (offset, limit) => new MovieBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.MovieExtension, (offset, limit) => new MovieExtensionBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.MovieExtensionHeader, (offset, limit) => new MovieExtensionHeaderBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.MovieFragment, (offset, limit) => new MovieFragmentBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.MovieFragmentHeader, (offset, limit) => new MovieFragmentHeaderBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.MovieFragmentRandomAccess, (offset, limit) => new MovieFragmentRandomAccessBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.MovieFragmentRandomAccessOffset, (offset, limit) => new MovieFragmentRandomAccessOffsetBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.MovieHeader, (offset, limit) => new MovieHeaderBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.NullMediaHeader, (offset, limit) => new NullMediaHeaderBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.OriginalFormat, (offset, limit) => new OriginalFormatBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.PartitionEntry, (offset, limit) => new PartitionEntryBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.PrimaryItemReference, (offset, limit) => new PrimaryItemReferenceBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.ProgressiveDownloadInfo, (offset, limit) => new ProgressiveDownloadInfoBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.ProgressiveDownloadInfo, (offset, limit) => new ProgressiveDownloadInfoBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.ProtectionSchemeInfo, (offset, limit) => new ProtectionSchemeInfoBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.SampleCompositionTime, (offset, limit) => new SampleCompositionTimeBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.SampleDecodingTime, (offset, limit) => new SampleDescriptionBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.SampleDegradationPriority, (offset, limit) => new SampleDegradationPriorityBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.SampleDescription, (offset, limit) => new SampleDescriptionBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.SampleGroupDescription, (offset, limit) => new SampleGroupDescriptionBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.SamplePaddingBits, (offset, limit) => new SamplePaddingBitsBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.SampleSize, (offset, limit) => new SampleSizeBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.CompactSampleSize, (offset, limit) => new CompactSampleSizeBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.SubSampleInformation, (offset, limit) => new SampleSubInfoBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.SampleTable, (offset, limit) => new SampleTableBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.SampleToChunk, (offset, limit) => new SampleToChunkBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.SampleToGroup, (offset, limit) => new SampleToGroupBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.SchemeInformation, (offset, limit) => new SchemeInformationBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.SchemeType, (offset, limit) => new SchemeTypeBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.ShadowSyncSampleTable, (offset, limit) => new ShadowSyncSampleTableBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.Skip, (offset, limit) => new SkipBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.SoundMediaHeader, (offset, limit) => new SoundMediaHeaderBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.SyncSampleTable, (offset, limit) => new SyncSampleTableBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.Track, (offset, limit) => new TrackBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.TrackExtension, (offset, limit) => new TrackExtensionBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.TrackFragment, (offset, limit) => new TrackFragmentBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.TrackFragmentHeader, (offset, limit) => new TrackFragmentHeaderBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.TrackFragmentRandomAccess, (offset, limit) => new TrackFragmentRandomAccessBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.TrackFragmentRun, (offset, limit) => new TrackFragmentRunBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.TrackHeader, (offset, limit) => new TrackHeaderBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.TrackReference, (offset, limit) => new TrackReferenceBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.TrackSelection, (offset, limit) => new TrackSelectionBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.UserData, (offset, limit) => new UserDataBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.VideoMediaHeader, (offset, limit) => new VideoMediaHeaderBox(offset, limit));
        _boxes.GetOrAdd(BmffBoxType.Xml, (offset, limit) => new XmlBox(offset, limit));
    }
}
