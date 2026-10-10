using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// The collection of tests that hold the thread pool with a <see cref="ThreadPoolSaturation"/>.
/// It runs after every parallel collection and alone, so no other test's work waits behind the
/// blockers.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadPoolSaturationCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Thread pool saturation";
}
