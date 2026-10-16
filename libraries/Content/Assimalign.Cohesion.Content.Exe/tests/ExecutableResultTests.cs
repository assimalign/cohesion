using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Exe.Tests;

public sealed class ExecutableResultTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Exe] - Create: results retain termination, output and timing")]
    [InlineData(ExecutableTerminationReason.Exited, 7)]
    [InlineData(ExecutableTerminationReason.TimedOut, -1)]
    [InlineData(ExecutableTerminationReason.Canceled, -1)]
    [InlineData(ExecutableTerminationReason.FailedToStart, null)]
    public void Create_TerminationOutcome_PreservesResult(ExecutableTerminationReason reason, int? exitCode)
    {
        // Arrange
        var start = new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.FromHours(-4));

        // Act
        var result = ExecutableResult.Create(exitCode, "out", "err", true, false, start, start.AddSeconds(2), reason);

        // Assert
        result.ExitCode.ShouldBe(exitCode);
        result.StandardOutput.ShouldBe("out");
        result.StandardError.ShouldBe("err");
        result.StandardOutputTruncated.ShouldBeTrue();
        result.StandardErrorTruncated.ShouldBeFalse();
        result.StartedAt.Offset.ShouldBe(TimeSpan.Zero);
        result.EndedAt.ShouldBe(start.AddSeconds(2));
        result.Duration.ShouldBe(TimeSpan.FromSeconds(2));
        result.TerminationReason.ShouldBe(reason);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Exe] - Create: inconsistent result states are rejected")]
    public void Create_InconsistentResult_Throws()
    {
        // Arrange
        var start = DateTimeOffset.UtcNow;

        // Act / Assert
        Should.Throw<ArgumentException>(() => ExecutableResult.Create(null, "", "", false, false, start, start, ExecutableTerminationReason.Exited));
        Should.Throw<ArgumentException>(() => ExecutableResult.Create(0, "", "", false, false, start, start, ExecutableTerminationReason.FailedToStart));
        Should.Throw<ArgumentOutOfRangeException>(() => ExecutableResult.Create(0, "", "", false, false, start, start.AddSeconds(-1), ExecutableTerminationReason.Exited));
        Should.Throw<ArgumentOutOfRangeException>(() => ExecutableResult.Create(0, "", "", false, false, start, start, (ExecutableTerminationReason)99));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Exe] - Create: start information copies caller collections")]
    public void Create_MutableInputs_CopiesCollections()
    {
        // Arrange
        var arguments = new List<string> { "space and quote\"" };
        var environment = new Dictionary<string, string?> { ["SET"] = "value", ["REMOVE"] = null };

        // Act
        var info = ExecutableStartInfo.Create("program", arguments, "directory", environment, "input", 12, 34, TimeSpan.FromSeconds(5));
        arguments[0] = "changed";
        environment["SET"] = "changed";

        // Assert
        info.ExecutablePath.ShouldBe("program");
        info.Arguments.ShouldBe(new[] { "space and quote\"" });
        info.Environment["SET"].ShouldBe("value");
        info.Environment["REMOVE"].ShouldBeNull();
        info.WorkingDirectory.ShouldBe("directory");
        info.StandardInput.ShouldBe("input");
        info.MaxStandardOutputBytes.ShouldBe(12);
        info.MaxStandardErrorBytes.ShouldBe(34);
        info.Timeout.ShouldBe(TimeSpan.FromSeconds(5));
        ((IList<string>)info.Arguments).IsReadOnly.ShouldBeTrue();
        ((IDictionary<string, string?>)info.Environment).IsReadOnly.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Exe] - Create: invalid invocation limits and arguments are rejected")]
    public void Create_InvalidStartOptions_Throws()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => ExecutableStartInfo.Create("program", maxStandardOutputBytes: -1));
        Should.Throw<ArgumentOutOfRangeException>(() => ExecutableStartInfo.Create("program", maxStandardErrorBytes: -1));
        Should.Throw<ArgumentOutOfRangeException>(() => ExecutableStartInfo.Create("program", timeout: TimeSpan.FromSeconds(-1)));
        Should.Throw<ArgumentException>(() => ExecutableStartInfo.Create("program", ["nul\0value"]));
        Should.Throw<ArgumentNullException>(() => ExecutableStartInfo.Create(null!));
    }
}
