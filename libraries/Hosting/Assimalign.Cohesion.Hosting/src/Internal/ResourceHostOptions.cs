using System;
using System.IO;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.Hosting;

internal enum ResourceHostFailureKind
{
    Configuration,
    Dependency,
}

internal enum ResourceHostStopSignal
{
    None,
    Interrupt,
    Terminate,
}

internal enum ResourceHostRunMode
{
    Process,
    InProcess,
}

internal sealed class ResourceHostOptions
{
    internal const int DefaultStopGraceSeconds = 30;
    internal const int MinimumStopGraceSeconds = 5;

    private readonly Func<Exception, ResourceHostFailureKind?> _exceptionClassifier;

    internal ResourceHostOptions(
        int stopGraceSeconds = DefaultStopGraceSeconds,
        string? contentRootPath = null,
        string? stopEventName = null,
        Action<string>? protocolLineWriter = null,
        Func<Exception, ResourceHostFailureKind?>? exceptionClassifier = null,
        Action<int>? exitCodeHandler = null,
        IResourceHostSignalSource? signalSource = null,
        ResourceHostRunMode runMode = ResourceHostRunMode.Process)
    {
        StopGraceSeconds = ValidateStopGraceSeconds(stopGraceSeconds);
        ContentRootPath = ResolveContentRootPath(contentRootPath);
        StopEventName = stopEventName ?? ResourceEnvironment.GetValue(ResourceEnvironment.StopEvent);
        ProtocolLineWriter = protocolLineWriter ?? Console.Out.WriteLine;
        _exceptionClassifier = exceptionClassifier ?? (static _ => null);
        ExitCodeHandler = exitCodeHandler ?? SetProcessExitCode;
        SignalSource = signalSource ?? ResourceHostSignalSource.Instance;
        RunMode = runMode;
    }

    internal int StopGraceSeconds { get; }

    internal TimeSpan ShutdownTimeout => DeriveShutdownTimeout(StopGraceSeconds);

    internal FileSystemPath ContentRootPath { get; }

    internal string? StopEventName { get; }

    internal Action<string> ProtocolLineWriter { get; }

    internal Action<int> ExitCodeHandler { get; }

    internal IResourceHostSignalSource SignalSource { get; }

    internal ResourceHostRunMode RunMode { get; }

    internal ResourceHostFailureKind? ClassifyException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        ResourceHostFailureKind? direct = _exceptionClassifier(exception);
        if (direct is not null)
        {
            return direct;
        }

        ResourceHostFailureKind? dependency = null;

        if (exception is AggregateException aggregateException)
        {
            foreach (Exception innerException in aggregateException.Flatten().InnerExceptions)
            {
                ResourceHostFailureKind? classified = ClassifyException(innerException);
                if (classified is ResourceHostFailureKind.Configuration)
                {
                    return classified;
                }

                dependency ??= classified;
            }

            return dependency;
        }

        return exception.InnerException is null
            ? null
            : ClassifyException(exception.InnerException);
    }

    internal static Func<Exception, ResourceHostFailureKind?> CreateExceptionClassifier<
        TConfigurationException,
        TDependencyException>()
        where TConfigurationException : Exception
        where TDependencyException : Exception
    {
        return static exception => exception switch
        {
            TConfigurationException => ResourceHostFailureKind.Configuration,
            TDependencyException => ResourceHostFailureKind.Dependency,
            _ => null,
        };
    }

    internal static TimeSpan DeriveShutdownTimeout(int stopGraceSeconds)
    {
        ValidateStopGraceSeconds(stopGraceSeconds);

        return TimeSpan.FromSeconds(Math.Max(MinimumStopGraceSeconds, stopGraceSeconds - 5));
    }

    internal static FileSystemPath ResolveContentRootPath(string? contentRootPath = null)
    {
        string? configuredPath = string.IsNullOrWhiteSpace(contentRootPath)
            ? ResourceEnvironment.GetValue(ResourceEnvironment.ContentRoot)
            : contentRootPath;
        string resolvedPath = string.IsNullOrWhiteSpace(configuredPath)
            ? AppContext.BaseDirectory
            : configuredPath;

        if (!Path.IsPathFullyQualified(resolvedPath))
        {
            throw new ArgumentException(
                $"The resource content root must be an absolute path: '{resolvedPath}'.",
                nameof(contentRootPath));
        }

        return FileSystemPath.Parse(Path.GetFullPath(resolvedPath));
    }

    private static int ValidateStopGraceSeconds(int stopGraceSeconds)
    {
        if (stopGraceSeconds < MinimumStopGraceSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stopGraceSeconds),
                stopGraceSeconds,
                $"The stop grace period must be at least {MinimumStopGraceSeconds} seconds.");
        }

        return stopGraceSeconds;
    }

    private static void SetProcessExitCode(int exitCode)
    {
        System.Environment.ExitCode = exitCode;
    }
}
