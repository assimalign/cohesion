using System;

namespace Assimalign.Cohesion.Database.Blob.Internal;

internal sealed class BlobUploadException : InvalidOperationException
{
    internal const string Code = "COHDBB007";

    internal BlobUploadException(string message) : base(message)
    {
    }
}
