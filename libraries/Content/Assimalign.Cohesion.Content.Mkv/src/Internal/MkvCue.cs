using Assimalign.Cohesion.Content.Ebml;

namespace Assimalign.Cohesion.Content.Mkv.Internal;

internal sealed record MkvCue(ulong TrackId, ulong Timestamp, EbmlElement Cluster);
