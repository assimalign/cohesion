using Xunit;

namespace Assimalign.Cohesion.Content.Mpeg.Tests;

// ArrayPool is shared with the rest of the process. Isolate the native allocation measurement
// so unrelated parser tests cannot empty its buckets between the small and large loads.
[CollectionDefinition("MP4 allocation", DisableParallelization = true)]
public sealed class Mp4AllocationCollection { }
