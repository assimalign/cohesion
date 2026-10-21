using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>Runs the worker give-up interleavings without parallel test collections.</summary>
[CollectionDefinition(nameof(DatabaseWorkerFailureWindowCollection), DisableParallelization = true)]
public sealed class DatabaseWorkerFailureWindowCollection
{
}
