namespace Assimalign.Cohesion.Files.Bmff;

public enum BmffBoxType : uint
{
    // Base Media File Format standard box types.

    /// <summary>
    /// Represents the file type and compatibility of the file.
    /// </summary>
    FileType                        = 0x66747970, // ftyp   - ISO/IEC 15596-12 4.3
    /// <summary>
    /// 
    /// </summary>
    FreeSpace                       = 0x66726565, // skip   - ISO/IEC 15596-12 8.1.2
    /// <summary>
    /// 
    /// </summary>
    Skip                            = 0x736b6970, // free   - ISO/IEC 15596-12 8.1.2
    /// <summary>
    /// Progression download information.
    /// </summary>
    ProgressiveDownloadInfo             = 0x7064696e, // pdin   - ISO/IEC 15596-12 8.1.3
    /// <summary>
    /// Container for all metadata
    /// </summary>
    Movie                           = 0x6d6f6f76, // moov   - ISO/IEC 15596-12 8.2.1
    /// <summary>
    /// Movie header,
    /// </summary>
    MovieHeader                     = 0x6d766864, // mvhd   - ISO/IEC 15596-12 8.2.2
    /// <summary>
    ///  The raw media data.
    /// </summary>
    MediaData                       = 0x6d646174, // mdat   - ISO/IEC 15596-12 8.2.2
    /// <summary>
    /// Container for an individual track or stream
    /// </summary>
    Track                           = 0x7472616b, // trak   - ISO/IEC 15596-12 8.3.1
    /// <summary>
    /// Track header which contains the overall information about the track.
    /// </summary>
    TrackHeader                     = 0x746b6864, // tkhd   - ISO/IEC 15596-12 8.3.2
    /// <summary>
    /// Track reference container.
    /// </summary>
    TrackReference                  = 0x74726566, // tref   - ISO/IEC 15596-12 8.3.3
    /// <summary>
    /// Container for the media information in a track.
    /// </summary>
    Media                           = 0x6d646961, // mdia   - ISO/IEC 15596-12 8.4
    /// <summary>
    /// Media header, overall information about the media.
    /// </summary>
    MediaHeader                     = 0x6d646864, // mdhd    - ISO/IEC 15596-12 8.4.2
    /// <summary>
    /// Indicates the media handler type.
    /// </summary>
    Handler                    = 0x68646c72, // hdlr   - ISO/IEC 15596-12 8.4.3
    /// <summary>
    /// Container for media information.
    /// </summary>
    MediaInfo                = 0x6d696e66, // minf   - ISO/IEC 15596-12 8.4.4
    /// <summary>
    /// 
    /// </summary>
    VideoMediaHeader                = 0x766d6864, // vmhd   - ISO/IEC 15596-12 8.4.5.2
    /// <summary>
    /// 
    /// </summary>
    SoundMediaHeader                = 0x736d6864, // smhd   - ISO/IEC 15596-12 8.4.5.3
    /// <summary>
    /// 
    /// </summary>
    HintMediaHeader                 = 0x686d6864, // hmhd   - ISO/IEC 15596-12 8.4.5.4
    /// <summary>
    /// 
    /// </summary>
    NullMediaHeader                 = 0x6e6d6864, // nmhd   - ISO/IEC 15596-12 8.4.5.5
    /// <summary>
    /// Data information container.
    /// </summary>
    DataInfo                 = 0x64696e66, // dinf   - ISO/IEC 15596-12 8.5
    /// <summary>
    /// sample table container for the time/space map
    /// </summary>
    SampleTable                     = 0x7374626c, // stbl   - ISO/IEC 15596-12 8.5
    /// <summary>
    /// sample descriptions (codec types, initialization, etc.)
    /// </summary>
    SampleDescription               = 0x73747364, // stsd   - ISO/IEC 15596-12 8.5.2
    SampleDecodingTime              = 0x73747473, // ISO/IEC 15596-12 8.6.1.2
    /// <summary>
    /// 
    /// </summary>
    SampleCompositionTime           = 0x63747473, // ctts  - ISO/IEC 15596-12 8.6.1.3
    SyncSampleTable                 = 0x73747373, // ISO/IEC 15596-12 8.6.2
    ShadowSyncSampleTable           = 0x73747368, // ISO/IEC 15596-12 8.6.3
    Edit                            = 0x65647473, // edts   - ISO/IEC 15596-12 8.6.4
    EditList                        = 0x656c7374, // elst   - ISO/IEC 15596-12 8.6.6
    DisposableSample                = 0x73647470, // ISO/IEC 15596-12 8.6.4
    DataReference                   = 0x64726566, // ISO/IEC 15596-12 8.7.2
    SampleSize                      = 0x7374737a, // ISO/IEC 15596-12 8.7.3.2
    CompactSampleSize               = 0x73747a32, // ISO/IEC 15596-12 8.7.3.3
    SampleToChunk                   = 0x73747363, // stsc  - ISO/IEC 15596-12 8.7.4
    ChunkOffset                     = 0x7374636f, // ISO/IEC 15596-12 8.7.5
    ChunkOffset6fBit                = 0x636f3634, // ISO/IEC 15596-12 8.7.5
    SamplePaddingBits               = 0x70616462, // ISO/IEC 15596-12 8.7.6
    SampleDegradationPriority       = 0x73746470, // ISO/IEC 15596-12 8.7.6
    SubSampleInformation            = 0x73756273, // ISO/IEC 15596-12 8.7.7
    MovieExtension                  = 0x6d766578, // ISO/IEC 15596-12 8.8.1
    MovieExtensionHeader            = 0x6d656864, // ISO/IEC 15596-12 8.8.2
    TrackExtension                  = 0x74726578, // ISO/IEC 15596-12 8.8.3
    MovieFragment                   = 0x6d6f6f66, // moof   - ISO/IEC 15596-12 8.8.4
    MovieFragmentHeader             = 0x6d666864, // ISO/IEC 15596-12 8.8.5
    TrackFragment                   = 0x74726166, // ISO/IEC 15596-12 8.8.6
    TrackFragmentHeader             = 0x74666864, // ISO/IEC 15596-12 8.8.7
    TrackFragmentRun                = 0x7472756e, // ISO/IEC 15596-12 8.8.8
    MovieFragmentRandomAccess       = 0x6d667261, // mfra   - ISO/IEC 15596-12 8.8.9
    TrackFragmentRandomAccess       = 0x74667261, // ISO/IEC 15596-12 8.8.10
    MovieFragmentRandomAccessOffset = 0x6d66726f, // ISO/IEC 15596-12 8.8.11
    SampleToGroup                   = 0x73626770, // ISO/IEC 15596-12 8.9.2
    SampleGroupDescription          = 0x73677064, // ISO/IEC 15596-12 8.9.3
    UserData                        = 0x75647461, // udta   - ISO/IEC 15596-12 8.10.1
    Copyright                            = 0x63707274, // ISO/IEC 15596-12 8.10.2
    TrackSelection                            = 0x7473656c, // ISO/IEC 15596-12 8.10.3
    Meta                            = 0x6d657461, // meta   - ISO/IEC 15596-12 8.11.1
    Xml                             = 0x786d6c20, // xml    - ISO/IEC 15596-12 8.11.2
    BinaryXml                       = 0x62786d6c, // bxml   - ISO/IEC 15596-12 8.11.2
    ItemLocation                    = 0x696c6f63, // iloc   - ISO/IEC 15596-12 8.11.3
    PrimaryItemReference            = 0x7069746d, // pitm   - ISO/IEC 15596-12 8.11.4
    ItemProtection                  = 0x6970726f, // ipro   - ISO/IEC 15596-12 8.11.5
    ItemInfo                 = 0x69696e66, // iinf   - ISO/IEC 15596-12 8.11.6
    AdditionalMeta                  = 0x6d65636f, // meco   - ISO/IEC 15596-12 8.11.7
    MetaRelationship                = 0x6d657265, // ISO/IEC 15596-12 8.11.8
    ProtectionSchemeInfo                            = 0x73696e66, // ISO/IEC 15596-12 8.12.1
    OriginalFormat                            = 0x66726d61, // ISO/IEC 15596-12 8.12.2
    IpmpInfo                            = 0x696d6966, // ISO/IEC 15596-12 8.12.3
    SchemeType                            = 0x7363686d, // ISO/IEC 15596-12 8.12.4
    IpmpControl                            = 0x69706d63, // ISO/IEC 15596-12 8.12.4
    SchemeInformation                            = 0x73636869, // ISO/IEC 15596-12 8.12.5
    FileDeliveryItemInfo                            = 0x6669696e, // ISO/IEC 15596-12 8.13.2
    PartitionEntry                            = 0x7061656e, // ISO/IEC 15596-12 8.13.2
    FilePartition                            = 0x66706172, // ISO/IEC 15596-12 8.13.3
    FecReservoir                            = 0x66656372, // ISO/IEC 15596-12 8.13.4
    FileDeliverySessionGroup                            = 0x73656772, // ISO/IEC 15596-12 8.13.5
    GroupIdToName                            = 0x6769746e, // ISO/IEC 15596-12 8.13.6
    uuid                            = 0x75756964, // ISO/IEC 15596-12 11.1
}
