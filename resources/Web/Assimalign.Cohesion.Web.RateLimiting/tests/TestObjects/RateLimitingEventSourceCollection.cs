using Xunit;

namespace Assimalign.Cohesion.Web.RateLimiting.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RateLimitingEventSourceCollection
{
    public const string Name = "RateLimiting event source";
}
