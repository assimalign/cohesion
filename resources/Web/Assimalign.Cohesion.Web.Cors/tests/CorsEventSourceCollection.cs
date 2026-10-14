using Xunit;

namespace Assimalign.Cohesion.Web.Cors.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CorsEventSourceCollection
{
    public const string Name = "Cors event source";
}
