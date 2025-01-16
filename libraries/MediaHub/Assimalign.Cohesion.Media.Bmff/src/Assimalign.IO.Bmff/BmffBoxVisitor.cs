using System;

namespace Assimalign.IO.Bmff;

public abstract class BmffBoxVisitor<T> : IBmffBoxVisitor<T>
{
    public virtual T Visit(BmffBox box)
    {
        ThrowIfNull(box);
        return box.Accept(this);
    }

    public virtual T Visit(AdditionalMetaBox box)
    {
        ThrowIfNull(box);
        throw new NotImplementedException();
    }

    public virtual T Visit(BinaryXmlBox box)
    {
        ThrowIfNull(box);
        throw new NotImplementedException();
    }

    public virtual T Visit(ChunkOffsetBox box)
    {
        ThrowIfNull(box);
        throw new NotImplementedException();
    }

    public T Visit(ChunkOffset64BitBox box)
    {
       
        ThrowIfNull(box);
        throw new NotImplementedException();
    }

    public T Visit(CopyrightBox box)
    {
       
        ThrowIfNull(box);
        throw new NotImplementedException();
    }

    public T Visit(DataInfoBox box)
    {
        ThrowIfNull(box);

        throw new NotImplementedException();
    }

    public T Visit(DataReferenceBox box)
    {
        ThrowIfNull(box);

        throw new NotImplementedException();
    }

    public T Visit(DisposableSampleBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(EditBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(EditListBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(FecReservoirBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(FileDeliveryItemInfoBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(FileDeliverySessionGroupBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(FilePartitionBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(FileTypeBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(FreeSpaceBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(GroupIdToNameBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(HandlerBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(HintMediaHeaderBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(IpmpControlBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(IpmpInfoBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(ItemLocationBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(ItemProtectionBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(MediaBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(MediaDataBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(MediaHeaderBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(MediaInfoBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(MetaRelationshipBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(MetaBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(MovieBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(MovieExtensionBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(MovieExtensionHeaderBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(MovieFragmentBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(MovieFragmentHeaderBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(MovieFragmentRandomAccessBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(MovieFragmentRandomAccessOffsetBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(MovieHeaderBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(NullMediaHeaderBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(OriginalFormatBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(PartitionEntryBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(PrimaryItemReferenceBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(ProgressiveDownloadInfoBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(ProtectionSchemeInfoBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(SampleCompositionTimeBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(SampleDecodingTimeBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(SampleDegradationPriorityBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(SampleDescriptionBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(SampleGroupDescriptionBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(SamplePaddingBitsBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(SampleSizeBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(SampleSubInfoBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(SampleTableBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(SampleToChunkBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(SampleToGroupBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(SchemeInformationBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(SchemeTypeBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(ShadowSyncSampleTableBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(SkipBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(SoundMediaHeaderBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(SyncSampleTableBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(TrackBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(TrackExtensionBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(TrackFragmentBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(TrackFragmentHeaderBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(TrackFragmentRandomAccessBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(TrackFragmentRunBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(TrackHeaderBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(TrackSelectionBox box)
    {
        throw new NotImplementedException();
    }

    public T Visit(UserDataBox box)
    {
        ThrowIfNull(box);

        foreach (var child in box.Children)
        {
            child.Accept(this);
        }

        return box.Accept(this);
    }

    public T Visit(VideoMediaHeaderBox box)
    {
        ThrowIfNull(box);

        return box.Accept(this);
    }

    public virtual T Visit(XmlBox box)
    {
        ThrowIfNull(box);

        return box.Accept(this);
    }

    private void ThrowIfNull(BmffBox box)
    {
        if (box is null)
        {
            throw new ArgumentNullException(nameof(box));
        }
    }
}
