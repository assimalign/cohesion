using System;

using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mpeg.Internal;

// A shared MediaSample has one PTS. Repeated/dwell/non-unit edit programs cannot be
// projected into that shape without duplicating samples or inventing timestamps.
internal sealed class Mp4EditMap
{
    internal MediaTimestamp Shift { get; private set; }
    internal MediaTimestamp? Start { get; private set; }
    internal MediaTimestamp? End { get; private set; }
    internal MediaTimestamp? Duration { get; private set; }
    internal bool Empty { get; private set; }
    internal long Position { get; }

    internal Mp4EditMap(EditListBox? edits, uint movieScale, uint mediaScale)
    {
        Position = edits?.Offset ?? 0;
        if (edits is null || edits.Entries.Length == 0) { return; }
        var elapsed = new MediaTimestamp(0, movieScale);
        var seenMedia = false;
        try
        {
            foreach (var entry in edits.Entries)
            {
                if (entry.SegmentDuration > long.MaxValue)
                {
                    throw new ContentFormatException(ErrorMessages.TimestampRange, ContentFormatErrorCode.InvalidValue, Position);
                }
                var duration = new MediaTimestamp((long)entry.SegmentDuration, movieScale);
                if (entry.MediaRateInteger != 1 || entry.MediaRateFraction != 0 || (entry.MediaTime != -1 && seenMedia))
                {
                    throw new ContentFormatException(ErrorMessages.EditUnsupported, ContentFormatErrorCode.Unsupported, Position);
                }
                if (entry.MediaTime != -1)
                {
                    if (entry.MediaTime < 0)
                    {
                        throw new ContentFormatException(ErrorMessages.EditInvalid, ContentFormatErrorCode.InvalidValue, Position);
                    }
                    seenMedia = true;
                    Start = elapsed;
                    End = elapsed + duration;
                    Shift = elapsed - new MediaTimestamp(entry.MediaTime, mediaScale);
                }
                elapsed += duration;
            }
        }
        catch (OverflowException error)
        {
            throw new ContentFormatException(ErrorMessages.TimestampRange, ContentFormatErrorCode.InvalidValue, Position, error);
        }
        Empty = !seenMedia;
        Duration = elapsed;
    }

    internal bool Presents(MediaTimestamp time) => !Empty && (!Start.HasValue || time >= Start.Value) && (!End.HasValue || time < End.Value);
}
