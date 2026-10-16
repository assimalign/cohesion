using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg;

/// <summary>MP4 registry, brand interpretation and document operations over the public BMFF seams.</summary>
public static partial class Mp4Profile
{
    /// <summary>Builds an independent BMFF registry extended with the retained MP4 boxes.</summary>
    /// <param name="options">Limits for sample-entry prefixes, or defaults. Pass the same limits to the BMFF reader.</param>
    /// <returns>An immutable profile registry; BMFF defaults are never changed.</returns>
    public static BmffBoxRegistry CreateRegistry(BmffReaderOptions? options = null)
    {
        var limits = options ?? new BmffReaderOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(limits.MaxBufferedPayloadSize);
        var builder = BmffBoxRegistry.CreateBuilder();
        builder.Register((BmffBoxType)FourCC.Parse("avc1").Value, header => new Avc1SampleEntryBox(header, limits.MaxBufferedPayloadSize));
        builder.Register((BmffBoxType)FourCC.Parse("avc3").Value, header => new Avc3SampleEntryBox(header, limits.MaxBufferedPayloadSize));
        builder.Register((BmffBoxType)FourCC.Parse("hvc1").Value, header => new Hvc1SampleEntryBox(header, limits.MaxBufferedPayloadSize));
        builder.Register((BmffBoxType)FourCC.Parse("hev1").Value, header => new Hev1SampleEntryBox(header, limits.MaxBufferedPayloadSize));
        builder.Register((BmffBoxType)FourCC.Parse("av01").Value, header => new Av01SampleEntryBox(header, limits.MaxBufferedPayloadSize));
        builder.Register((BmffBoxType)FourCC.Parse("vp09").Value, header => new Vp09SampleEntryBox(header, limits.MaxBufferedPayloadSize));
        builder.Register((BmffBoxType)FourCC.Parse("mp4a").Value, header => new Mp4aSampleEntryBox(header, limits.MaxBufferedPayloadSize));
        builder.Register((BmffBoxType)FourCC.Parse("Opus").Value, header => new OpusSampleEntryBox(header, limits.MaxBufferedPayloadSize));
        builder.Register((BmffBoxType)FourCC.Parse("ac-3").Value, header => new Ac3SampleEntryBox(header, limits.MaxBufferedPayloadSize));
        builder.Register((BmffBoxType)FourCC.Parse("ec-3").Value, header => new Ec3SampleEntryBox(header, limits.MaxBufferedPayloadSize));
        builder.Register((BmffBoxType)FourCC.Parse("wvtt").Value, header => new WvttSampleEntryBox(header, limits.MaxBufferedPayloadSize));
        builder.Register((BmffBoxType)FourCC.Parse("stpp").Value, header => new StppSampleEntryBox(header, limits.MaxBufferedPayloadSize));
        builder.Register((BmffBoxType)FourCC.Parse("tx3g").Value, header => new Tx3gSampleEntryBox(header, limits.MaxBufferedPayloadSize));
        builder.Register((BmffBoxType)FourCC.Parse("avcC").Value, header => new AvcConfigurationBox(header));
        builder.Register((BmffBoxType)FourCC.Parse("hvcC").Value, header => new HevcConfigurationBox(header));
        builder.Register((BmffBoxType)FourCC.Parse("av1C").Value, header => new Av1ConfigurationBox(header));
        builder.Register((BmffBoxType)FourCC.Parse("vpcC").Value, header => new VpConfigurationBox(header));
        builder.Register((BmffBoxType)FourCC.Parse("pasp").Value, header => new PixelAspectRatioBox(header));
        builder.Register((BmffBoxType)FourCC.Parse("colr").Value, header => new ColourInformationBox(header));
        builder.Register((BmffBoxType)FourCC.Parse("btrt").Value, header => new BitRateBox(header));
        builder.Register((BmffBoxType)FourCC.Parse("esds").Value, header => new ElementaryStreamDescriptorBox(header));
        builder.Register((BmffBoxType)FourCC.Parse("dOps").Value, header => new OpusSpecificBox(header));
        builder.Register((BmffBoxType)FourCC.Parse("dac3").Value, header => new Ac3SpecificBox(header));
        builder.Register((BmffBoxType)FourCC.Parse("dec3").Value, header => new Ec3SpecificBox(header));
        return builder.Build();
    }

    /// <summary>Interprets a retained MP4RA brand without inferring codec validity.</summary>
    /// <param name="brand">The raw brand, including significant spaces.</param>
    /// <returns>Its known declarations, or None for an unknown code.</returns>
    public static Mp4BrandFeatures InterpretBrand(FourCC brand) => brand.ToString() switch
    {
        "isom" or "iso2" or "iso3" or "iso4" or "iso5" or "iso6" or "iso7" or "iso8" or "iso9" => Mp4BrandFeatures.IsoBaseMedia,
        "mp41" => Mp4BrandFeatures.IsoBaseMedia | Mp4BrandFeatures.Mp4Version1,
        "mp42" => Mp4BrandFeatures.IsoBaseMedia | Mp4BrandFeatures.Mp4Version2,
        "avc1" => Mp4BrandFeatures.IsoBaseMedia | Mp4BrandFeatures.Avc,
        "M4A " => Mp4BrandFeatures.IsoBaseMedia | Mp4BrandFeatures.AudioOnly,
        "M4V " => Mp4BrandFeatures.IsoBaseMedia | Mp4BrandFeatures.Video,
        "dash" or "msdh" or "msix" => Mp4BrandFeatures.IsoBaseMedia | Mp4BrandFeatures.Dash | Mp4BrandFeatures.Fragmented,
        "cmfc" => Mp4BrandFeatures.IsoBaseMedia | Mp4BrandFeatures.Fragmented | Mp4BrandFeatures.Cmaf,
        "cmf2" => Mp4BrandFeatures.IsoBaseMedia | Mp4BrandFeatures.Fragmented | Mp4BrandFeatures.CmafVersion2,
        "cmfs" => Mp4BrandFeatures.IsoBaseMedia | Mp4BrandFeatures.Fragmented | Mp4BrandFeatures.CmafSegment,
        "cmfl" => Mp4BrandFeatures.IsoBaseMedia | Mp4BrandFeatures.Fragmented | Mp4BrandFeatures.CmafChunk,
        _ => Mp4BrandFeatures.None
    };

    /// <summary>Loads an editable BMFF document with typed MP4 sample entries and lazy decoder configuration.</summary>
    /// <param name="source">A readable, seekable source at its first box.</param>
    /// <param name="options">BMFF limits; unknown sample entries remain preserved.</param>
    /// <param name="leaveOpen">Whether the document borrows the source.</param>
    /// <param name="cancellationToken">Cancellation between boxes and sample entries.</param>
    /// <returns>The caller-owned editable document.</returns>
    /// <exception cref="ContentFormatException">The container or entry is malformed or exceeds limits.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static BmffDocument LoadDocument(Stream source, BmffReaderOptions? options = null, bool leaveOpen = true, CancellationToken cancellationToken = default)
    {
        var limits = options ?? new BmffReaderOptions();
        var registry = CreateRegistry(limits);
        var document = BmffDocument.LoadAsync(source, limits, registry, leaveOpen, cancellationToken).GetAwaiter().GetResult();
        try
        {
            BindEntries(document.Boxes, source, limits, registry, 0, cancellationToken);
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private static void BindEntries(IList<BmffBox> boxes, Stream source, BmffReaderOptions limits, BmffBoxRegistry registry, int depth, CancellationToken cancellationToken)
    {
        foreach (var box in boxes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (box is SampleDescriptionBox description)
            {
                for (var index = 0; index < description.Children.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = description.Children[index];
                    if (entry.BoxType.ToFourCC().ToString() is not ("avc1" or "avc3" or "hvc1" or "hev1" or "av01" or "vp09" or "mp4a" or "Opus" or "ac-3" or "ec-3" or "wvtt" or "stpp" or "tx3g"))
                    {
                        continue;
                    }
                    // The core deliberately preserves every stsd child. Re-enter its public reader
                    // at that already validated header so the profile registration takes effect.
                    source.Position = entry.Offset;
                    var nestedLimits = new BmffReaderOptions
                    {
                        MaxBufferedPayloadSize = limits.MaxBufferedPayloadSize,
                        MaxTableEntryCount = limits.MaxTableEntryCount,
                        MaxDepth = Math.Max(0, limits.MaxDepth - depth - 1),
                        Strict = false
                    };
                    using var reader = BmffReader.Create(source, nestedLimits, registry);
                    if (reader.Read()) { description.Children[index] = reader.Current; }
                }
            }
            else if (box is BmffBoxComposite composite)
            {
                BindEntries(composite.Children, source, limits, registry, depth + 1, cancellationToken);
            }
        }
    }
}

