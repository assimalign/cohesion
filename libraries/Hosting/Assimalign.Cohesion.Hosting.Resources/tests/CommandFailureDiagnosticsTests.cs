using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public sealed class CommandFailureDiagnosticsTests
{
    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Command diagnostics: reused exceptions retain separate invocation markers")]
    public async Task MarkReported_ConcurrentInvocations_ShouldConsumeEachExactlyOnce()
    {
        // Arrange
        var exception = new ArgumentException("private handler data");
        ResourceCommand[] commands = Enumerable.Range(0, 32).Select(index => CreateCommand(index.ToString())).ToArray();

        // Act
        await Task.WhenAll(commands.Select(command => Task.Run(() => CommandFailureDiagnostics.MarkReported(exception, command))));
        bool[] first = commands.Select(command => CommandFailureDiagnostics.TryConsume(exception, command)).ToArray();
        bool[] second = commands.Select(command => CommandFailureDiagnostics.TryConsume(exception, command)).ToArray();

        // Assert
        first.ShouldAllBe(value => value);
        second.ShouldAllBe(value => !value);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Command diagnostics: equal commands from different invocations cannot suppress each other")]
    public void TryConsume_EqualButDistinctCommand_ShouldLeaveOriginalMarker()
    {
        // Arrange
        var exception = new ArgumentException("private handler data");
        ResourceCommand original = CreateCommand("same-id");
        ResourceCommand later = original with { };
        CommandFailureDiagnostics.MarkReported(exception, original);

        // Act / Assert
        CommandFailureDiagnostics.TryConsume(exception, later).ShouldBeFalse();
        CommandFailureDiagnostics.TryConsume(exception, null).ShouldBeFalse();
        CommandFailureDiagnostics.TryConsume(exception, original).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Hosting.Resources] - Command diagnostics: custom exception metadata cannot replace a failure")]
    [InlineData(false)]
    [InlineData(true)]
    public void MarkReported_UnusableExceptionData_ShouldFallBackWithoutThrowing(bool throws)
    {
        // Arrange
        var exception = new CustomDataException(throws);
        ResourceCommand command = CreateCommand("custom-data");

        // Act
        Should.NotThrow(() => CommandFailureDiagnostics.MarkReported(exception, command));

        // Assert: diagnostics are best effort for application-defined metadata carriers.
        CommandFailureDiagnostics.TryConsume(exception, command).ShouldBeFalse();
    }

    private static ResourceCommand CreateCommand(string id) => new(id, "known", "owner", "key", ReadOnlyMemory<byte>.Empty);

    private sealed class CustomDataException : ArgumentException
    {
        private readonly bool _throws;
        private readonly IDictionary _readOnly = new ReadOnlyDictionary<object, object>(new Dictionary<object, object>());

        internal CustomDataException(bool throws)
        {
            _throws = throws;
        }

        public override IDictionary Data => _throws ? throw new InvalidOperationException("custom metadata failure") : _readOnly;
    }
}
