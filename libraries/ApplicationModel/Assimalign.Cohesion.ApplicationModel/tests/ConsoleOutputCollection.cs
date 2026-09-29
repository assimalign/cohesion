using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// Serializes tests that temporarily replace the process-wide console output writer.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleOutputCollection
{
    /// <summary>
    /// The xUnit collection name.
    /// </summary>
    public const string Name = "Console output";
}
