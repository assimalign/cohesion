using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>Supplies a transport completion fact independently from its pipe's buffered bytes.</summary>
internal sealed class TestConnectionInputCompletion : IConnectionInputCompletion
{
    public bool IsInputCompleted { get; set; }
}
