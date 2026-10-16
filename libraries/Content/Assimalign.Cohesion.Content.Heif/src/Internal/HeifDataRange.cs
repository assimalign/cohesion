using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif.Internal;

internal readonly record struct HeifDataRange(BmffBox Box, long Start, long Length);
