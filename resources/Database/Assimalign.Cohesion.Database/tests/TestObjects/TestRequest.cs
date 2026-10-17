using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// A typed request the base suite executes: the request root of <see cref="TestSession"/>.
/// </summary>
internal sealed class TestRequest : DatabaseRequest
{
    public TestRequest()
    {
    }
}
