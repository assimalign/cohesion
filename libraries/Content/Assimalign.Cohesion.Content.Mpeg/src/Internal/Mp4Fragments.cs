using System;
using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg.Internal;

internal static class Mp4Fragments
{
    internal static void Bind(BmffDocument document, MovieBox movie, Dictionary<uint, Mp4Track> tracks, Mp4ReaderOptions options, CancellationToken token)
    {
        var defaults = new Dictionary<uint, TrackExtensionBox>();
        var extension = Mp4Tree.Child<MovieExtensionBox>(movie.Children);
        if (extension is not null)
        {
            foreach (var box in extension.Children)
            {
                if (box is TrackExtensionBox trex)
                {
                    if (!tracks.ContainsKey(trex.TrackId) || !defaults.TryAdd(trex.TrackId, trex))
                    {
                        throw new ContentFormatException(ErrorMessages.FragmentInvalid, ContentFormatErrorCode.InvalidStructure, trex.Offset);
                    }
                }
            }
        }
        var runCount = 0;
        var boxes = document.Boxes;
        for (var top = 0; top < boxes.Count; top++)
        {
            token.ThrowIfCancellationRequested();
            if (boxes[top] is not MovieFragmentBox moof) { continue; }
            _ = Mp4Tree.Required<MovieFragmentHeaderBox>(moof);
            var upper = document.Source!.Length;
            for (var next = top + 1; next < boxes.Count; next++)
            {
                if (boxes[next] is MovieFragmentBox) { upper = boxes[next].Offset; break; }
            }
            var previousDataEnd = moof.Offset;
            var trafOrdinal = 0;
            foreach (var child in moof.Children)
            {
                token.ThrowIfCancellationRequested();
                if (child is not TrackFragmentBox traf) { continue; }
                var tfhd = Mp4Tree.Required<TrackFragmentHeaderBox>(traf);
                if (!tracks.TryGetValue(tfhd.TrackId, out var track) || !defaults.TryGetValue(tfhd.TrackId, out var trex))
                {
                    throw new ContentFormatException(ErrorMessages.FragmentInvalid, ContentFormatErrorCode.InvalidStructure, tfhd.Offset);
                }
                var description = tfhd.SampleDescriptionIndex ?? trex.DefaultSampleDescriptionIndex;
                if (description == 0 || description > track.DescriptionCount)
                {
                    throw new ContentFormatException(ErrorMessages.FragmentInvalid, ContentFormatErrorCode.InvalidStructure, tfhd.Offset);
                }
                var tfdt = Mp4Tree.Child<TrackFragmentDecodeTimeBox>(traf.Children);
                var dts = tfdt is null ? track.Index.DecodeEnd : tfdt.BaseMediaDecodeTime <= long.MaxValue ?
                    (long)tfdt.BaseMediaDecodeTime : throw new ContentFormatException(ErrorMessages.TimestampRange, ContentFormatErrorCode.InvalidValue, tfdt.Offset);
                if (dts < track.Index.DecodeEnd)
                {
                    throw new ContentFormatException(ErrorMessages.FragmentInvalid, ContentFormatErrorCode.InvalidStructure, tfdt?.Offset ?? tfhd.Offset);
                }
                long baseOffset;
                if (tfhd.BaseDataOffset.HasValue)
                {
                    if (tfhd.BaseDataOffset.Value > long.MaxValue) { throw new ContentFormatException(ErrorMessages.SampleRangeInvalid, ContentFormatErrorCode.InvalidStructure, tfhd.Offset); }
                    baseOffset = (long)tfhd.BaseDataOffset.Value;
                }
                else { baseOffset = (tfhd.Flags & 0x20000) != 0 || trafOrdinal == 0 ? moof.Offset : previousDataEnd; }
                var dataEnd = baseOffset;
                foreach (var item in traf.Children)
                {
                    token.ThrowIfCancellationRequested();
                    if (item is not TrackFragmentRunBox trun) { continue; }
                    if (++runCount > options.MaxFragmentRunCount)
                    {
                        throw new ContentFormatException(ErrorMessages.RunLimit, ContentFormatErrorCode.LimitExceeded, trun.Offset);
                    }
                    if ((tfhd.Flags & 0x10000) != 0 && trun.Entries.Length != 0)
                    {
                        throw new ContentFormatException(ErrorMessages.FragmentInvalid, ContentFormatErrorCode.InvalidStructure, trun.Offset);
                    }
                    try
                    {
                        var offset = trun.DataOffset.HasValue ? checked(baseOffset + trun.DataOffset.Value) : dataEnd;
                        var run = new Mp4FragmentRun(trun, track.Index.TotalCount, offset, dts,
                            tfhd.DefaultSampleDuration ?? trex.DefaultSampleDuration,
                            tfhd.DefaultSampleSize ?? trex.DefaultSampleSize,
                            tfhd.DefaultSampleFlags ?? trex.DefaultSampleFlags,
                            moof.Offset, checked(moof.Offset + moof.Limit));
                        long size = 0;
                        for (var i = 0; i < trun.Entries.Length; i++)
                        {
                            token.ThrowIfCancellationRequested();
                            size = checked(size + run.SampleSize(i));
                            dts = checked(dts + run.SampleDuration(i));
                        }
                        dataEnd = checked(offset + size);
                        if (trun.Entries.Length != 0 && track.IsLocal && !Mp4Tree.InMedia(boxes, offset, size, moof.Offset, upper))
                        {
                            throw new ContentFormatException(ErrorMessages.SampleRangeInvalid, ContentFormatErrorCode.InvalidStructure, trun.Offset);
                        }
                        track.Index.AddRun(run, dts, description == track.DescriptionIndex &&
                            !Mp4Tree.Contains(traf.Children, 0x73677064) && !Mp4Tree.Contains(traf.Children, 0x73626770));
                    }
                    catch (OverflowException error)
                    {
                        throw new ContentFormatException(ErrorMessages.TimestampRange, ContentFormatErrorCode.InvalidValue, trun.Offset, error);
                    }
                }
                previousDataEnd = dataEnd;
                trafOrdinal++;
            }
            if (trafOrdinal == 0) { throw new ContentFormatException(ErrorMessages.FragmentInvalid, ContentFormatErrorCode.InvalidStructure, moof.Offset); }
        }
    }
}

