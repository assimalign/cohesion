using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// A typed request the base suite executes.
/// </summary>
internal sealed class TestRequest : QueryRequest
{
    public TestRequest()
        : base(new TestStatement())
    {
    }

    private sealed class TestStatement : QueryStatement
    {
        public override QueryExpression Expression { get; } = new TestExpression();

        private sealed class TestExpression : QueryExpression
        {
        }
    }
}
