using System;

namespace Assimalign.Cohesion.Content.Mpeg;

/// <summary>Recognized declarations, not a codec-conformance certificate.</summary>
[Flags]
public enum Mp4BrandFeatures
{
    /// <summary>No retained brand recognized.</summary>
    None = 0,
    /// <summary>ISO base media file format compatibility.</summary>
    IsoBaseMedia = 1,
    /// <summary>MP4 version one.</summary>
    Mp4Version1 = 2,
    /// <summary>MP4 version two.</summary>
    Mp4Version2 = 4,
    /// <summary>AVC extensions.</summary>
    Avc = 8,
    /// <summary>Audio-only presentation.</summary>
    AudioOnly = 16,
    /// <summary>Video presentation.</summary>
    Video = 32,
    /// <summary>DASH file/segment compatibility.</summary>
    Dash = 64,
    /// <summary>Fragmented media.</summary>
    Fragmented = 128,
    /// <summary>CMAF structural brand cmfc.</summary>
    Cmaf = 256,
    /// <summary>CMAF structural brand cmf2.</summary>
    CmafVersion2 = 512,
    /// <summary>CMAF segment brand cmfs.</summary>
    CmafSegment = 1024,
    /// <summary>CMAF chunk brand cmfl.</summary>
    CmafChunk = 2048
}
