using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>An immutable registry of explicit BMFF box factories.</summary>
public sealed class BmffBoxRegistry
{
    private readonly FrozenDictionary<BmffBoxType, Func<BmffBoxHeader, BmffBox>> _factories;
    private readonly FrozenSet<BmffBoxType> _unsupported;
    internal BmffBoxRegistry(Dictionary<BmffBoxType, Func<BmffBoxHeader, BmffBox>> factories, HashSet<BmffBoxType> unsupported)
    {
        _factories = factories.ToFrozenDictionary();
        _unsupported = unsupported.ToFrozenSet();
    }
    /// <summary>Gets the shared registry with every package box class registered.</summary>
    public static BmffBoxRegistry Default { get; } = BuildDefault();
    /// <summary>Creates a builder, optionally seeded with this package's registrations.</summary>
    /// <param name="includeDefaults">Whether to include the default factories.</param>
    /// <returns>A mutable builder with no shared mutable state.</returns>
    public static BmffBoxRegistryBuilder CreateBuilder(bool includeDefaults = true)
    {
        var builder = new BmffBoxRegistryBuilder();
        if (includeDefaults)
        {
            foreach (var entry in Default._factories)
            {
                builder.Factories.Add(entry.Key, entry.Value);
            }
            builder.Unsupported.UnionWith(Default._unsupported);
        }
        return builder;
    }
    internal bool CanParse(BmffBoxType type) => _factories.ContainsKey(type) && !_unsupported.Contains(type);
    internal BmffBox? Create(BmffBoxHeader header)
    {
        if (!_factories.TryGetValue(header.BoxType, out var factory))
        {
            return null;
        }
        var box = factory(header);
        if (box is null || box.BoxType != header.BoxType)
        {
            throw new InvalidOperationException(ErrorMessages.InvalidFactory);
        }
        box.Header = header;
        return box;
    }
    private static BmffBoxRegistry BuildDefault()
    {
        var builder = new BmffBoxRegistryBuilder();
        builder.Register(BmffBoxType.AdditionalMeta, header => new AdditionalMetaBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.BinaryXml, header => new BinaryXmlBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.ChunkOffset, header => new ChunkOffsetBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.ChunkOffset6fBit, header => new ChunkOffset64BitBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.Copyright, header => new CopyrightBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.DataInfo, header => new DataInfoBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.DataReference, header => new DataReferenceBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.DisposableSample, header => new DisposableSampleBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.Edit, header => new EditBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.EditList, header => new EditListBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.FecReservoir, header => new FecReservoirBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Unsupported.Add(BmffBoxType.FecReservoir);
        builder.Register(BmffBoxType.FileDeliveryItemInfo, header => new FileDeliveryItemInfoBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Unsupported.Add(BmffBoxType.FileDeliveryItemInfo);
        builder.Register(BmffBoxType.FileDeliverySessionGroup, header => new FileDeliverySessionGroupBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Unsupported.Add(BmffBoxType.FileDeliverySessionGroup);
        builder.Register(BmffBoxType.FilePartition, header => new FilePartitionBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Unsupported.Add(BmffBoxType.FilePartition);
        builder.Register(BmffBoxType.FileType, header => new FileTypeBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.FreeSpace, header => new FreeSpaceBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.GroupIdToName, header => new GroupIdToNameBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Unsupported.Add(BmffBoxType.GroupIdToName);
        builder.Register(BmffBoxType.Handler, header => new HandlerBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.HintMediaHeader, header => new HintMediaHeaderBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.IpmpControl, header => new IpmpControlBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Unsupported.Add(BmffBoxType.IpmpControl);
        builder.Register(BmffBoxType.IpmpInfo, header => new IpmpInfoBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Unsupported.Add(BmffBoxType.IpmpInfo);
        builder.Register(BmffBoxType.ItemInfo, header => new ItemInfoBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.ItemLocation, header => new ItemLocationBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.ItemProtection, header => new ItemProtectionBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.Media, header => new MediaBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.MediaData, header => new MediaDataBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.MediaHeader, header => new MediaHeaderBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.MediaInfo, header => new MediaInfoBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.Meta, header => new MetaBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.MetaRelationship, header => new MetaRelationshipBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Unsupported.Add(BmffBoxType.MetaRelationship);
        builder.Register(BmffBoxType.Movie, header => new MovieBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.MovieExtension, header => new MovieExtensionBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.MovieExtensionHeader, header => new MovieExtensionHeaderBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.MovieFragment, header => new MovieFragmentBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.MovieFragmentHeader, header => new MovieFragmentHeaderBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.MovieFragmentRandomAccess, header => new MovieFragmentRandomAccessBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.MovieFragmentRandomAccessOffset, header => new MovieFragmentRandomAccessOffsetBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.MovieHeader, header => new MovieHeaderBox(size: header.TotalSize ?? 0, offset: header.Offset));
        builder.Register(BmffBoxType.NullMediaHeader, header => new NullMediaHeaderBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.OriginalFormat, header => new OriginalFormatBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.PartitionEntry, header => new PartitionEntryBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.PrimaryItemReference, header => new PrimaryItemReferenceBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.ProgressiveDownloadInfo, header => new ProgressiveDownloadInfoBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.ProtectionSchemeInfo, header => new ProtectionSchemeInfoBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.SampleCompositionTime, header => new SampleCompositionTimeBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.SampleDecodingTime, header => new SampleDecodingTimeBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.SampleDegradationPriority, header => new SampleDegradationPriorityBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Unsupported.Add(BmffBoxType.SampleDegradationPriority);
        builder.Register(BmffBoxType.SampleDescription, header => new SampleDescriptionBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.SampleGroupDescription, header => new SampleGroupDescriptionBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.SamplePaddingBits, header => new SamplePaddingBitsBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Unsupported.Add(BmffBoxType.SamplePaddingBits);
        builder.Register(BmffBoxType.SampleSize, header => new SampleSizeBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.CompactSampleSize, header => new CompactSampleSizeBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.SubSampleInformation, header => new SampleSubInfoBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.SampleTable, header => new SampleTableBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.SampleToChunk, header => new SampleToChunkBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.SampleToGroup, header => new SampleToGroupBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.SchemeInformation, header => new SchemeInformationBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.SchemeType, header => new SchemeTypeBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.ShadowSyncSampleTable, header => new ShadowSyncSampleTableBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Unsupported.Add(BmffBoxType.ShadowSyncSampleTable);
        builder.Register(BmffBoxType.Skip, header => new SkipBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.SoundMediaHeader, header => new SoundMediaHeaderBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.SyncSampleTable, header => new SyncSampleTableBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.Track, header => new TrackBox(size: header.TotalSize ?? 0, offset: header.Offset));
        builder.Register(BmffBoxType.TrackExtension, header => new TrackExtensionBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.TrackFragment, header => new TrackFragmentBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.TrackFragmentHeader, header => new TrackFragmentHeaderBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.TrackFragmentRandomAccess, header => new TrackFragmentRandomAccessBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.TrackFragmentRun, header => new TrackFragmentRunBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.TrackHeader, header => new TrackHeaderBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.TrackReference, header => new TrackReferenceBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.TrackSelection, header => new TrackSelectionBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Unsupported.Add(BmffBoxType.TrackSelection);
        builder.Register(BmffBoxType.UserData, header => new UserDataBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.VideoMediaHeader, header => new VideoMediaHeaderBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.Xml, header => new XmlBox(offset: header.Offset, limit: header.TotalSize ?? 0));
        builder.Register(BmffBoxType.TrackFragmentDecodeTime, header => new TrackFragmentDecodeTimeBox(header.Offset, header.TotalSize ?? 0));
        builder.Register(BmffBoxType.DataEntryUrl, header => new DataEntryUrlBox(header.Offset, header.TotalSize ?? 0));
        builder.Register(BmffBoxType.DataEntryUrn, header => new DataEntryUrnBox(header.Offset, header.TotalSize ?? 0));
        builder.Register(BmffBoxType.ItemInfoEntry, header => new ItemInfoEntryBox(header.Offset, header.TotalSize ?? 0));
        return builder.Build();
    }
}
