using Xunit;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Tests;

/// <summary>
/// Serializes the tests that observe the document model's event source: the source is
/// process-wide.
/// </summary>
[CollectionDefinition(nameof(DocumentDatabaseEventSourceCollection), DisableParallelization = true)]
public class DocumentDatabaseEventSourceCollection
{
}
