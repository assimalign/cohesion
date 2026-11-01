using System;
using System.Numerics;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Protocol.Tests;

/// <summary>
/// Both dispatchers test an action's kind with <c>kind.HasFlag((ApplicationActionKind)operation)</c>,
/// so each operation must equal the single kind flag of the same name.
/// </summary>
public class ApplicationActionOperationTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationActionOperation: ";

    [Theory(DisplayName = DisplayPrefix + "Each operation has the value of the kind flag of the same name")]
    [InlineData(ApplicationActionOperation.Read, ApplicationActionKind.Read)]
    [InlineData(ApplicationActionOperation.Write, ApplicationActionKind.Write)]
    public void Value_OfEachOperation_ShouldEqualTheKindFlagOfTheSameName(ApplicationActionOperation operation, ApplicationActionKind kind)
    {
        // Arrange, Act & Assert
        ((int)operation).ShouldBe((int)kind);
        operation.ToString().ShouldBe(kind.ToString());
    }

    [Fact(DisplayName = DisplayPrefix + "Every operation is exactly one kind flag")]
    public void Values_OfEveryOperation_ShouldBeSingleKindFlags()
    {
        // Arrange
        ApplicationActionOperation[] operations = Enum.GetValues<ApplicationActionOperation>();

        // Act & Assert
        operations.Length.ShouldBe(2);
        foreach (ApplicationActionOperation operation in operations)
        {
            BitOperations.PopCount((uint)operation).ShouldBe(1);
            Enum.IsDefined((ApplicationActionKind)operation).ShouldBeTrue();
            Enum.GetName((ApplicationActionKind)operation).ShouldBe(operation.ToString());
        }
    }
}
