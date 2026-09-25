using System;

namespace Assimalign.Cohesion.Web.Compression.Internal;

/// <summary>
/// Internal signal thrown by <see cref="LimitedDecompressionStream"/> when a decompressed request
/// body exceeds the configured size guard. It never escapes the package: the request-decompression
/// middleware catches it and translates it into <c>413 Content Too Large</c>.
/// </summary>
internal sealed class RequestDecompressionLimitException : Exception
{
    public RequestDecompressionLimitException(long limit)
        : base($"The decompressed request body exceeded the configured limit of {limit} bytes.")
    {
    }
}
