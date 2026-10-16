namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Identifies standard BMFF boxes by their raw four-byte codes; unregistered codes remain representable.</summary>
public enum BmffBoxType : uint
{
    // Base Media File Format standard box types.

    /// <summary>
    /// Represents the file type and compatibility of the file.
    /// </summary>
    FileType                        = 0x66747970, // ftyp   - ISO/IEC 14496-12 4.3
    /// <summary>Unused space (<c>free</c>).</summary>
    FreeSpace                       = 0x66726565, // free   - ISO/IEC 14496-12 8.1.2
    /// <summary>Unused space to skip (<c>skip</c>).</summary>
    Skip                            = 0x736b6970, // skip   - ISO/IEC 14496-12 8.1.2
    /// <summary>
    /// Progression download information.
    /// </summary>
    ProgressiveDownloadInfo             = 0x7064696e, // pdin   - ISO/IEC 14496-12 8.1.3
    /// <summary>
    /// Container for all metadata
    /// </summary>
    Movie                           = 0x6d6f6f76, // moov   - ISO/IEC 14496-12 8.2.1
    /// <summary>
    /// Movie header,
    /// </summary>
    MovieHeader                     = 0x6d766864, // mvhd   - ISO/IEC 14496-12 8.2.2
    /// <summary>
    ///  The raw media data.
    /// </summary>
    MediaData                       = 0x6d646174, // mdat   - ISO/IEC 14496-12 8.2.2
    /// <summary>
    /// Container for an individual track or stream
    /// </summary>
    Track                           = 0x7472616b, // trak   - ISO/IEC 14496-12 8.3.1
    /// <summary>
    /// Track header which contains the overall information about the track.
    /// </summary>
    TrackHeader                     = 0x746b6864, // tkhd   - ISO/IEC 14496-12 8.3.2
    /// <summary>
    /// Track reference container.
    /// </summary>
    TrackReference                  = 0x74726566, // tref   - ISO/IEC 14496-12 8.3.3
    /// <summary>
    /// Container for the media information in a track.
    /// </summary>
    Media                           = 0x6d646961, // mdia   - ISO/IEC 14496-12 8.4
    /// <summary>
    /// Media header, overall information about the media.
    /// </summary>
    MediaHeader                     = 0x6d646864, // mdhd    - ISO/IEC 14496-12 8.4.2
    /// <summary>
    /// Indicates the media handler type.
    /// </summary>
    Handler                    = 0x68646c72, // hdlr   - ISO/IEC 14496-12 8.4.3
    /// <summary>
    /// Container for media information.
    /// </summary>
    MediaInfo                = 0x6d696e66, // minf   - ISO/IEC 14496-12 8.4.4
    /// <summary>Video media header (<c>vmhd</c>).</summary>
    VideoMediaHeader                = 0x766d6864, // vmhd   - ISO/IEC 14496-12 8.4.5.2
    /// <summary>Sound media header (<c>smhd</c>).</summary>
    SoundMediaHeader                = 0x736d6864, // smhd   - ISO/IEC 14496-12 8.4.5.3
    /// <summary>Hint media header (<c>hmhd</c>).</summary>
    HintMediaHeader                 = 0x686d6864, // hmhd   - ISO/IEC 14496-12 8.4.5.4
    /// <summary>Null media header (<c>nmhd</c>).</summary>
    NullMediaHeader                 = 0x6e6d6864, // nmhd   - ISO/IEC 14496-12 8.4.5.5
    /// <summary>
    /// Data information container.
    /// </summary>
    DataInfo                 = 0x64696e66, // dinf   - ISO/IEC 14496-12 8.5
    /// <summary>
    /// sample table container for the time/space map
    /// </summary>
    SampleTable                     = 0x7374626c, // stbl   - ISO/IEC 14496-12 8.5
    /// <summary>
    /// sample descriptions (codec types, initialization, etc.)
    /// </summary>
    SampleDescription               = 0x73747364, // stsd   - ISO/IEC 14496-12 8.5.2
    /// <summary>Sample decoding time (<c>stts</c>).</summary>
    SampleDecodingTime              = 0x73747473, // ISO/IEC 14496-12 8.6.1.2
    /// <summary>Sample composition time (<c>ctts</c>).</summary>
    SampleCompositionTime           = 0x63747473, // ctts  - ISO/IEC 14496-12 8.6.1.3
    /// <summary>Sync sample table (<c>stss</c>).</summary>
    SyncSampleTable                 = 0x73747373, // ISO/IEC 14496-12 8.6.2
    /// <summary>Shadow sync sample table (<c>stsh</c>).</summary>
    ShadowSyncSampleTable           = 0x73747368, // ISO/IEC 14496-12 8.6.3
    /// <summary>Edit (<c>edts</c>).</summary>
    Edit                            = 0x65647473, // edts   - ISO/IEC 14496-12 8.6.4
    /// <summary>Edit list (<c>elst</c>).</summary>
    EditList                        = 0x656c7374, // elst   - ISO/IEC 14496-12 8.6.6
    /// <summary>Sample dependency flags (<c>sdtp</c>).</summary>
    DisposableSample                = 0x73647470, // ISO/IEC 14496-12 8.6.4
    /// <summary>Data reference (<c>dref</c>).</summary>
    DataReference                   = 0x64726566, // ISO/IEC 14496-12 8.7.2
    /// <summary>Sample size (<c>stsz</c>).</summary>
    SampleSize                      = 0x7374737a, // ISO/IEC 14496-12 8.7.3.2
    /// <summary>Compact sample size (<c>stz2</c>).</summary>
    CompactSampleSize               = 0x73747a32, // ISO/IEC 14496-12 8.7.3.3
    /// <summary>Sample to chunk (<c>stsc</c>).</summary>
    SampleToChunk                   = 0x73747363, // stsc  - ISO/IEC 14496-12 8.7.4
    /// <summary>Chunk offset (<c>stco</c>).</summary>
    ChunkOffset                     = 0x7374636f, // ISO/IEC 14496-12 8.7.5
    /// <summary>64-bit chunk offsets (<c>co64</c>).</summary>
    ChunkOffset6fBit                = 0x636f3634, // ISO/IEC 14496-12 8.7.5
    /// <summary>Sample padding bits (<c>padb</c>).</summary>
    SamplePaddingBits               = 0x70616462, // ISO/IEC 14496-12 8.7.6
    /// <summary>Sample degradation priority (<c>stdp</c>).</summary>
    SampleDegradationPriority       = 0x73746470, // ISO/IEC 14496-12 8.7.6
    /// <summary>Sub sample information (<c>subs</c>).</summary>
    SubSampleInformation            = 0x73756273, // ISO/IEC 14496-12 8.7.7
    /// <summary>Movie extension (<c>mvex</c>).</summary>
    MovieExtension                  = 0x6d766578, // ISO/IEC 14496-12 8.8.1
    /// <summary>Movie extension header (<c>mehd</c>).</summary>
    MovieExtensionHeader            = 0x6d656864, // ISO/IEC 14496-12 8.8.2
    /// <summary>Track extension (<c>trex</c>).</summary>
    TrackExtension                  = 0x74726578, // ISO/IEC 14496-12 8.8.3
    /// <summary>Movie fragment (<c>moof</c>).</summary>
    MovieFragment                   = 0x6d6f6f66, // moof   - ISO/IEC 14496-12 8.8.4
    /// <summary>Movie fragment header (<c>mfhd</c>).</summary>
    MovieFragmentHeader             = 0x6d666864, // ISO/IEC 14496-12 8.8.5
    /// <summary>Track fragment (<c>traf</c>).</summary>
    TrackFragment                   = 0x74726166, // ISO/IEC 14496-12 8.8.6
    /// <summary>Track fragment header (<c>tfhd</c>).</summary>
    TrackFragmentHeader             = 0x74666864, // ISO/IEC 14496-12 8.8.7
    /// <summary>Track fragment run (<c>trun</c>).</summary>
    TrackFragmentRun                = 0x7472756e, // ISO/IEC 14496-12 8.8.8
    /// <summary>Movie fragment random access (<c>mfra</c>).</summary>
    MovieFragmentRandomAccess       = 0x6d667261, // mfra   - ISO/IEC 14496-12 8.8.9
    /// <summary>Track fragment random access (<c>tfra</c>).</summary>
    TrackFragmentRandomAccess       = 0x74667261, // ISO/IEC 14496-12 8.8.10
    /// <summary>Movie fragment random access offset (<c>mfro</c>).</summary>
    MovieFragmentRandomAccessOffset = 0x6d66726f, // ISO/IEC 14496-12 8.8.11
    /// <summary>Sample to group (<c>sbgp</c>).</summary>
    SampleToGroup                   = 0x73626770, // ISO/IEC 14496-12 8.9.2
    /// <summary>Sample group description (<c>sgpd</c>).</summary>
    SampleGroupDescription          = 0x73677064, // ISO/IEC 14496-12 8.9.3
    /// <summary>User data (<c>udta</c>).</summary>
    UserData                        = 0x75647461, // udta   - ISO/IEC 14496-12 8.10.1
    /// <summary>Copyright (<c>cprt</c>).</summary>
    Copyright                            = 0x63707274, // ISO/IEC 14496-12 8.10.2
    /// <summary>Track selection (<c>tsel</c>).</summary>
    TrackSelection                            = 0x7473656c, // ISO/IEC 14496-12 8.10.3
    /// <summary>Meta (<c>meta</c>).</summary>
    Meta                            = 0x6d657461, // meta   - ISO/IEC 14496-12 8.11.1
    /// <summary>Xml (<c>xml </c>).</summary>
    Xml                             = 0x786d6c20, // xml    - ISO/IEC 14496-12 8.11.2
    /// <summary>Binary xml (<c>bxml</c>).</summary>
    BinaryXml                       = 0x62786d6c, // bxml   - ISO/IEC 14496-12 8.11.2
    /// <summary>Item location (<c>iloc</c>).</summary>
    ItemLocation                    = 0x696c6f63, // iloc   - ISO/IEC 14496-12 8.11.3
    /// <summary>Primary item identity (<c>pitm</c>).</summary>
    PrimaryItemReference            = 0x7069746d, // pitm   - ISO/IEC 14496-12 8.11.4
    /// <summary>Item protection (<c>ipro</c>).</summary>
    ItemProtection                  = 0x6970726f, // ipro   - ISO/IEC 14496-12 8.11.5
    /// <summary>Item info (<c>iinf</c>).</summary>
    ItemInfo                 = 0x69696e66, // iinf   - ISO/IEC 14496-12 8.11.6
    /// <summary>Additional meta (<c>meco</c>).</summary>
    AdditionalMeta                  = 0x6d65636f, // meco   - ISO/IEC 14496-12 8.11.7
    /// <summary>Meta relationship (<c>mere</c>).</summary>
    MetaRelationship                = 0x6d657265, // ISO/IEC 14496-12 8.11.8
    /// <summary>Protection scheme info (<c>sinf</c>).</summary>
    ProtectionSchemeInfo                            = 0x73696e66, // ISO/IEC 14496-12 8.12.1
    /// <summary>Original format (<c>frma</c>).</summary>
    OriginalFormat                            = 0x66726d61, // ISO/IEC 14496-12 8.12.2
    /// <summary>Intellectual property management information (<c>imif</c>).</summary>
    IpmpInfo                            = 0x696d6966, // ISO/IEC 14496-12 8.12.3
    /// <summary>Scheme type (<c>schm</c>).</summary>
    SchemeType                            = 0x7363686d, // ISO/IEC 14496-12 8.12.4
    /// <summary>Intellectual property management control (<c>ipmc</c>).</summary>
    IpmpControl                            = 0x69706d63, // ISO/IEC 14496-12 8.12.4
    /// <summary>Scheme information (<c>schi</c>).</summary>
    SchemeInformation                            = 0x73636869, // ISO/IEC 14496-12 8.12.5
    /// <summary>File delivery item info (<c>fiin</c>).</summary>
    FileDeliveryItemInfo                            = 0x6669696e, // ISO/IEC 14496-12 8.13.2
    /// <summary>Partition entry (<c>paen</c>).</summary>
    PartitionEntry                            = 0x7061656e, // ISO/IEC 14496-12 8.13.2
    /// <summary>File partition (<c>fpar</c>).</summary>
    FilePartition                            = 0x66706172, // ISO/IEC 14496-12 8.13.3
    /// <summary>Forward error correction reservoir (<c>fecr</c>).</summary>
    FecReservoir                            = 0x66656372, // ISO/IEC 14496-12 8.13.4
    /// <summary>File delivery session group (<c>segr</c>).</summary>
    FileDeliverySessionGroup                            = 0x73656772, // ISO/IEC 14496-12 8.13.5
    /// <summary>Group id to name (<c>gitn</c>).</summary>
    GroupIdToName                            = 0x6769746e, // ISO/IEC 14496-12 8.13.6
    /// <summary>Track fragment decode time; ISO/IEC 14496-12 8.8.12.</summary>
    TrackFragmentDecodeTime = 0x74666474,
    /// <summary>URL data entry; ISO/IEC 14496-12 8.7.2.</summary>
    DataEntryUrl = 0x75726c20,
    /// <summary>URN data entry; ISO/IEC 14496-12 8.7.2.</summary>
    DataEntryUrn = 0x75726e20,
    /// <summary>Item information entry; ISO/IEC 14496-12 8.11.6.</summary>
    ItemInfoEntry = 0x696e6665,
    /// <summary>User-defined extended type (<c>uuid</c>).</summary>
    uuid                            = 0x75756964, // ISO/IEC 14496-12 11.1
}
