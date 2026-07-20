using System;

namespace Assimalign.Cohesion.Web.Compression.Internal;

/// <summary>
/// Internal signal thrown by <see cref="LimitedDecompressionStream"/> when the coded request body is
/// malformed and a decoder rejects it. It never escapes the package: the request-decompression
/// middleware catches it and translates it into <c>400 Bad Request</c>.
/// </summary>
internal sealed class RequestDecompressionFormatException : Exception
{
    public RequestDecompressionFormatException(Exception innerException)
        : base("The request body could not be decompressed; the coded content is malformed.", innerException)
    {
    }
}
