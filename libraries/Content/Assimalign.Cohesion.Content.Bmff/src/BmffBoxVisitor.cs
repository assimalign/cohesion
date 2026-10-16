using System;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>A visitor with safe defaults that traverse composites and return default.</summary>
/// <typeparam name="T">The result type.</typeparam>
public abstract class BmffBoxVisitor<T> : IBmffBoxVisitor<T>
{
    /// <summary>Visits children in order, returning the default result.</summary>
    /// <param name="box">The current node.</param>
    /// <returns>The default result.</returns>
    /// <exception cref="ArgumentNullException">The box is null.</exception>
    protected virtual T VisitDefault(BmffBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        if (box is BmffBoxComposite composite)
        {
            foreach (var child in composite.Children)
            {
                child.Accept(this);
            }
        }
        return default!;
    }
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(BmffBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(AdditionalMetaBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(BinaryXmlBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(ChunkOffsetBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(ChunkOffset64BitBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(CopyrightBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(DataInfoBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(DataReferenceBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(DisposableSampleBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(EditBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(EditListBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(FecReservoirBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(FileDeliveryItemInfoBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(FileDeliverySessionGroupBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(FilePartitionBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(FileTypeBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(FreeSpaceBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(GroupIdToNameBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(HandlerBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(HintMediaHeaderBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(IpmpControlBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(IpmpInfoBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(ItemInfoBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(ItemLocationBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(ItemProtectionBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(MediaBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(MediaDataBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(MediaHeaderBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(MediaInfoBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(MetaBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(MetaRelationshipBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(MovieBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(MovieExtensionBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(MovieExtensionHeaderBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(MovieFragmentBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(MovieFragmentHeaderBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(MovieFragmentRandomAccessBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(MovieFragmentRandomAccessOffsetBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(MovieHeaderBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(NullMediaHeaderBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(OriginalFormatBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(PartitionEntryBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(PrimaryItemReferenceBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(ProgressiveDownloadInfoBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(ProtectionSchemeInfoBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(SampleCompositionTimeBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(SampleDecodingTimeBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(SampleDegradationPriorityBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(SampleDescriptionBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(SampleGroupDescriptionBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(SamplePaddingBitsBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(SampleSizeBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(CompactSampleSizeBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(SampleSubInfoBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(SampleTableBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(SampleToChunkBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(SampleToGroupBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(SchemeInformationBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(SchemeTypeBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(ShadowSyncSampleTableBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(SkipBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(SoundMediaHeaderBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(SyncSampleTableBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(TrackBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(TrackExtensionBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(TrackFragmentBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(TrackFragmentHeaderBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(TrackFragmentRandomAccessBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(TrackFragmentRunBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(TrackHeaderBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(TrackReferenceBox box) => VisitDefault(box);

    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(TrackReferenceTypeBox box) => VisitDefault(box);

    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(TrackFragmentDecodeTimeBox box) => VisitDefault(box);

    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(DataEntryUrlBox box) => VisitDefault(box);

    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(DataEntryUrnBox box) => VisitDefault(box);

    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(ItemInfoEntryBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(TrackSelectionBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(UserDataBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(VideoMediaHeaderBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(XmlBox box) => VisitDefault(box);
    /// <inheritdoc/>
    /// <exception cref="System.ArgumentNullException">The box is null.</exception>
    public virtual T Visit(UnknownBox box) => VisitDefault(box);
}
