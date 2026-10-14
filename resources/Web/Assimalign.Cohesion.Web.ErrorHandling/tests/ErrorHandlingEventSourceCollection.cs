using Xunit;

namespace Assimalign.Cohesion.Web.ErrorHandling.Tests;

/// <summary>Serializes tests observing the process-wide ErrorHandling event source and counters.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ErrorHandlingEventSourceCollection
{
    public const string Name = "Web.ErrorHandling event source";
}
