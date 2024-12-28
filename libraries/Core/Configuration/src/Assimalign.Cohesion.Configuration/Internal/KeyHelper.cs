using System;

namespace Assimalign.Cohesion.Configuration.Internal;

internal static class KeyHelper
{
    private static ReadOnlySpan<char> Separators => ['\\', '/', ':', '.'];
}
