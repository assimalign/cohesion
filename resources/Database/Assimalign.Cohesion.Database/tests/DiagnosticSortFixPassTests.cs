using System.Linq;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

public sealed class DiagnosticSortFixPassTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Diagnostics: located maximum offsets precede unlocated entries stably")]
    public void Constructor_MaximumOffset_ShouldSortLocatedFirstStably()
    {
        // Arrange
        Diagnostic[] diagnostics =
        [
            new() { Code = "unlocated" },
            new() { Code = "located", Start = int.MaxValue, End = int.MaxValue },
            new() { Code = "same", Start = int.MaxValue, End = int.MaxValue },
            new() { Code = "unlocated2" },
        ];

        // Act
        var failure = new DatabaseException("failure", diagnostics);

        // Assert
        failure.Diagnostics.Select(item => item.Code).ShouldBe(new[] { "located", "same", "unlocated", "unlocated2" });
    }
}
