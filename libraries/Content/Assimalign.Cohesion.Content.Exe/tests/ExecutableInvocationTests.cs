using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Exe.Tests;

public sealed class ExecutableInvocationTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Exe] - InvokeAsync: exit codes are retained")]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(42)]
    public async Task InvokeAsync_ExitCode_ReturnsExit(int code)
    {
        // Arrange
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var info = ExecutableStartInfo.Create(TestHost.DotnetPath, TestHost.Arguments("exit", code.ToString()), timeout: TimeSpan.FromSeconds(5));

        // Act
        var result = await new ProcessExecutableInvoker().InvokeAsync(info, watchdog.Token);

        // Assert
        result.TerminationReason.ShouldBe(ExecutableTerminationReason.Exited);
        result.ExitCode.ShouldBe(code);
        result.StandardOutput.ShouldBeEmpty();
        result.StandardError.ShouldBeEmpty();
        result.Duration.ShouldBeGreaterThan(TimeSpan.Zero);
        result.Duration.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Exe] - InvokeAsync: argument list round-trips spaces quotes Unicode and shell characters")]
    public async Task InvokeAsync_ArgumentList_RoundTripsExactly()
    {
        // Arrange
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        string[] arguments = ["", "two words", "quote\"inside", "ends\\", "C:\\path with spaces\\", "雪🙂", "line\nbreak", "$(exit 99); & | < >"];
        var hostArguments = new List<string> { "echo" };
        hostArguments.AddRange(arguments);
        var info = ExecutableStartInfo.Create(TestHost.DotnetPath, TestHost.Arguments(hostArguments.ToArray()), timeout: TimeSpan.FromSeconds(5));

        // Act
        var result = await new ProcessExecutableInvoker().InvokeAsync(info, watchdog.Token);

        // Assert
        result.TerminationReason.ShouldBe(ExecutableTerminationReason.Exited);
        result.ExitCode.ShouldBe(0);
        var lines = result.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        lines.Length.ShouldBe(arguments.Length + 1);
        for (var i = 0; i < arguments.Length; i++)
        {
            TestHost.Decode(lines[i]).ShouldBe(arguments[i]);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Exe] - InvokeAsync: stdin is streamed and closed")]
    public async Task InvokeAsync_StandardInput_RoundTripsAndCloses()
    {
        // Arrange
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var input = "雪🙂\n" + new string('a', 256 * 1024);
        var info = ExecutableStartInfo.Create(TestHost.DotnetPath, TestHost.Arguments("stdin"), standardInput: input, timeout: TimeSpan.FromSeconds(5));

        // Act
        var result = await new ProcessExecutableInvoker().InvokeAsync(info, watchdog.Token);

        // Assert
        result.TerminationReason.ShouldBe(ExecutableTerminationReason.Exited);
        result.StandardOutput.ShouldBe(input);
        result.StandardOutputTruncated.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Exe] - InvokeAsync: absent stdin is closed")]
    public async Task InvokeAsync_NoStandardInput_ClosesPipe()
    {
        // Arrange
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var info = ExecutableStartInfo.Create(TestHost.DotnetPath, TestHost.Arguments("stdin"), timeout: TimeSpan.FromSeconds(5));

        // Act
        var result = await new ProcessExecutableInvoker().InvokeAsync(info, watchdog.Token);

        // Assert
        result.TerminationReason.ShouldBe(ExecutableTerminationReason.Exited);
        result.StandardOutput.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Exe] - InvokeAsync: each output stream respects its byte cap")]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1234, 4321)]
    public async Task InvokeAsync_OutputOverLimit_TruncatesAndContinuesDraining(int outputLimit, int errorLimit)
    {
        // Arrange
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var info = ExecutableStartInfo.Create(TestHost.DotnetPath, TestHost.Arguments("write", "100000", "120000"),
            maxStandardOutputBytes: outputLimit, maxStandardErrorBytes: errorLimit, timeout: TimeSpan.FromSeconds(5));

        // Act
        var result = await new ProcessExecutableInvoker().InvokeAsync(info, watchdog.Token);

        // Assert
        result.TerminationReason.ShouldBe(ExecutableTerminationReason.Exited);
        result.StandardOutput.ShouldBe(new string('o', outputLimit));
        result.StandardError.ShouldBe(new string('e', errorLimit));
        result.StandardOutputTruncated.ShouldBeTrue();
        result.StandardErrorTruncated.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Exe] - InvokeAsync: exact output caps are not truncation")]
    public async Task InvokeAsync_OutputAtLimit_DoesNotFlagTruncation()
    {
        // Arrange
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var info = ExecutableStartInfo.Create(TestHost.DotnetPath, TestHost.Arguments("write", "1024", "2048"),
            maxStandardOutputBytes: 1024, maxStandardErrorBytes: 2048, timeout: TimeSpan.FromSeconds(5));

        // Act
        var result = await new ProcessExecutableInvoker().InvokeAsync(info, watchdog.Token);

        // Assert
        result.TerminationReason.ShouldBe(ExecutableTerminationReason.Exited);
        result.StandardOutput.Length.ShouldBe(1024);
        result.StandardError.Length.ShouldBe(2048);
        result.StandardOutputTruncated.ShouldBeFalse();
        result.StandardErrorTruncated.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Exe] - InvokeAsync: heavy concurrent pipes do not deadlock")]
    public async Task InvokeAsync_HeavyOutputAndUnreadInput_DrainsConcurrently()
    {
        // Arrange
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var info = ExecutableStartInfo.Create(TestHost.DotnetPath, TestHost.Arguments("write", "4194304", "4194304"),
            standardInput: new string('i', 1024 * 1024), maxStandardOutputBytes: 4096, maxStandardErrorBytes: 4096,
            timeout: TimeSpan.FromSeconds(5));

        // Act
        var result = await new ProcessExecutableInvoker().InvokeAsync(info, watchdog.Token);

        // Assert
        result.TerminationReason.ShouldBe(ExecutableTerminationReason.Exited);
        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldBe(new string('o', 4096));
        result.StandardError.ShouldBe(new string('e', 4096));
        result.StandardOutputTruncated.ShouldBeTrue();
        result.StandardErrorTruncated.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Exe] - InvokeAsync: working directory and environment overrides are explicit")]
    public async Task InvokeAsync_EnvironmentAndDirectory_AppliesOverridesAndRemoval()
    {
        // Arrange
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var directory = Path.Combine(Path.GetTempPath(), "cohesion-exe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "working-directory-marker");
        try
        {
            await File.WriteAllTextAsync(marker, "fixture directory", watchdog.Token);
            var environment = new Dictionary<string, string?> { ["PATH"] = null, ["COHESION_EXE_TEST"] = "value 雪" };
            var info = ExecutableStartInfo.Create(TestHost.DotnetPath, TestHost.Arguments("context", "PATH", "COHESION_EXE_TEST"),
                directory, environment, timeout: TimeSpan.FromSeconds(5));

            // Act
            var result = await new ProcessExecutableInvoker().InvokeAsync(info, watchdog.Token);

            // Assert
            result.TerminationReason.ShouldBe(ExecutableTerminationReason.Exited);
            var lines = result.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            // macOS may canonicalize /var to /private/var in getcwd. Verify the selected directory's
            // unique identity and contents without assuming identical spellings through symlinks.
            var reportedDirectory = TestHost.Decode(lines[0]);
            Path.GetFileName(reportedDirectory).ShouldBe(Path.GetFileName(directory));
            (await File.ReadAllTextAsync(Path.Combine(reportedDirectory, "working-directory-marker"), watchdog.Token)).ShouldBe("fixture directory");
            TestHost.Decode(lines[1]).ShouldBe("<unset>");
            TestHost.Decode(lines[2]).ShouldBe("value 雪");
        }
        finally
        {
            File.Delete(marker);
            Directory.Delete(directory);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Exe] - InvokeAsync: timeout and cancellation terminate sleep")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_Sleep_TerminatesPromptly(bool cancel)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(cancel ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(8));
        var info = ExecutableStartInfo.Create(TestHost.DotnetPath, TestHost.Arguments("sleep", "10000"),
            timeout: cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(250));

        // Act
        var result = await new ProcessExecutableInvoker().InvokeAsync(info, cancellation.Token);

        // Assert
        result.TerminationReason.ShouldBe(cancel ? ExecutableTerminationReason.Canceled : ExecutableTerminationReason.TimedOut);
        result.ExitCode.ShouldNotBeNull();
        result.Duration.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Theory(DisplayName = "Cohesion Test [Content.Exe] - InvokeAsync: pre-cancellation and zero timeout prevent launch")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_Preterminated_DoesNotLaunch(bool cancel)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        if (cancel) { cancellation.Cancel(); }
        var info = ExecutableStartInfo.Create("missing-" + Guid.NewGuid(), timeout: TimeSpan.Zero);

        // Act
        var result = await new ProcessExecutableInvoker().InvokeAsync(info, cancellation.Token);

        // Assert
        result.TerminationReason.ShouldBe(cancel ? ExecutableTerminationReason.Canceled : ExecutableTerminationReason.TimedOut);
        result.ExitCode.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Exe] - InvokeAsync: OS launch failures return failed-start results")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_MissingOrNonExecutable_ReturnsFailedStart(bool createFile)
    {
        // Arrange
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var path = Path.Combine(Path.GetTempPath(), "cohesion-not-executable-" + Guid.NewGuid().ToString("N"));
        if (createFile) { await File.WriteAllTextAsync(path, "This is test data, not an executable.", watchdog.Token); }
        try
        {
            var info = ExecutableStartInfo.Create(path, timeout: TimeSpan.FromSeconds(5));

            // Act
            var result = await new ProcessExecutableInvoker().InvokeAsync(info, watchdog.Token);

            // Assert
            result.TerminationReason.ShouldBe(ExecutableTerminationReason.FailedToStart);
            result.ExitCode.ShouldBeNull();
            result.StandardOutput.ShouldBeEmpty();
            result.StandardError.ShouldBeEmpty();
        }
        finally { File.Delete(path); }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Exe] - InvokeAsync: cleanup kills the child and grandchild")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_ProcessTree_LeavesNoDescendants(bool cancel)
    {
        // Arrange
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var directory = Path.Combine(Path.GetTempPath(), "cohesion-exe-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var processes = new List<Process>();
        var info = ExecutableStartInfo.Create(TestHost.DotnetPath, TestHost.Arguments("tree", directory),
            timeout: cancel ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(3));
        var invocation = new ProcessExecutableInvoker().InvokeAsync(info, cancellation.Token).AsTask();
        try
        {
            foreach (var name in new[] { "tree", "child", "grandchild" })
            {
                var process = await TestHost.WaitForProcessAsync(Path.Combine(directory, name + ".pid"), invocation, watchdog.Token);
                processes.Add(process);
                process.HasExited.ShouldBeFalse();
            }

            // Act: the cancellation exception unwinds the same finally path as timeout and other failures.
            if (cancel) { await cancellation.CancelAsync(); }
            var result = await invocation.WaitAsync(watchdog.Token);

            // Assert
            result.TerminationReason.ShouldBe(cancel ? ExecutableTerminationReason.Canceled : ExecutableTerminationReason.TimedOut);
            foreach (var process in processes)
            {
                await process.WaitForExitAsync(watchdog.Token);
                process.HasExited.ShouldBeTrue();
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            // Emergency cleanup is limited to handles for PIDs produced by this fixture's unique directory.
            for (var i = processes.Count - 1; i >= 0; i--)
            {
                using var process = processes[i];
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            await invocation.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            foreach (var file in Directory.EnumerateFiles(directory)) { File.Delete(file); }
            Directory.Delete(directory);
        }
    }
}
