using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>A request of no language: a blob session has none, so it refuses every request.</summary>
internal sealed class BlobRequest : QueryRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BlobRequest"/> class: a request of no language.
    /// </summary>
    public BlobRequest()
        : base(null!)
    {
    }
}
