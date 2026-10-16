using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mpeg.Internal;

internal static class Mp4Conformance
{
    internal static IReadOnlyList<ContentFormatException> Check(Mp4File file, MovieBox movie, FileTypeBox brands, bool strict, CancellationToken token)
    {
        var diagnostics = new List<ContentFormatException>();
        var features = file.BrandFeatures;
        var hasVideo = false;
        foreach (var track in file.Tracks) { hasVideo |= track.Kind == MediaTrackKind.Video; }
        if ((features & Mp4BrandFeatures.AudioOnly) != 0 && hasVideo) { Report(diagnostics, strict, brands.Offset); }
        if ((features & Mp4BrandFeatures.Video) != 0 && !hasVideo) { Report(diagnostics, strict, brands.Offset); }
        if ((features & Mp4BrandFeatures.Fragmented) != 0 && !file.IsFragmented) { Report(diagnostics, strict, brands.Offset); }
        var cmaf = (features & (Mp4BrandFeatures.Cmaf | Mp4BrandFeatures.CmafVersion2 | Mp4BrandFeatures.CmafSegment | Mp4BrandFeatures.CmafChunk)) != 0;
        if (!cmaf) { return diagnostics.AsReadOnly(); }
        var extension = Mp4Tree.Child<MovieExtensionBox>(movie.Children);
        if (file.Tracks.Count != 1 || extension is null) { Report(diagnostics, strict, movie.Offset); }
        foreach (var track in file.Tracks)
        {
            var hasDefaults = false;
            if (extension is not null)
            {
                foreach (var box in extension.Children)
                {
                    if (box is TrackExtensionBox trex && trex.TrackId == track.Id) { hasDefaults = true; }
                }
            }
            if (!hasDefaults) { Report(diagnostics, strict, movie.Offset); }
            if (track is Mp4Track native && (!native.IsLocal || native.Index.ProgressiveCount != 0 || native.DescriptionCount != 1))
            {
                Report(diagnostics, strict, movie.Offset);
            }
        }
        uint? sequence = null;
        var fragmentCount = 0;
        var boxes = file.Document.Boxes;
        for (var i = 0; i < boxes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (boxes[i] is not MovieFragmentBox moof) { continue; }
            fragmentCount++;
            var header = Mp4Tree.Required<MovieFragmentHeaderBox>(moof);
            if (sequence.HasValue && header.SequenceNumber <= sequence.Value) { Report(diagnostics, strict, header.Offset); }
            sequence = header.SequenceNumber;
            if (i + 1 >= boxes.Count || boxes[i + 1] is not MediaDataBox) { Report(diagnostics, strict, moof.Offset); }
            var trafCount = 0;
            foreach (var child in moof.Children)
            {
                if (child is not TrackFragmentBox traf) { continue; }
                trafCount++;
                var tfhd = Mp4Tree.Required<TrackFragmentHeaderBox>(traf);
                if ((tfhd.Flags & 0x20000) == 0 || tfhd.BaseDataOffset.HasValue || Mp4Tree.Child<TrackFragmentDecodeTimeBox>(traf.Children) is null)
                {
                    Report(diagnostics, strict, tfhd.Offset);
                }
                var first = true;
                foreach (var item in traf.Children)
                {
                    if (item is not TrackFragmentRunBox run) { continue; }
                    if (first && !run.DataOffset.HasValue) { Report(diagnostics, strict, run.Offset); }
                    first = false;
                }
                if (first) { Report(diagnostics, strict, traf.Offset); }
            }
            if (trafCount != 1) { Report(diagnostics, strict, moof.Offset); }
        }
        if ((features & (Mp4BrandFeatures.CmafSegment | Mp4BrandFeatures.CmafChunk)) != 0 && fragmentCount == 0)
        {
            Report(diagnostics, strict, brands.Offset);
        }
        return diagnostics.AsReadOnly();
    }

    private static void Report(List<ContentFormatException> diagnostics, bool strict, long position)
    {
        var error = new ContentFormatException(ErrorMessages.BrandStructureMismatch, ContentFormatErrorCode.InvalidStructure, position);
        if (strict) { throw error; }
        if (diagnostics.Count == 256)
        {
            throw new ContentFormatException(ErrorMessages.DiagnosticLimit, ContentFormatErrorCode.LimitExceeded, position);
        }
        diagnostics.Add(error);
    }
}
