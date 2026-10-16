using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.Content.Exe;

/// <summary>Immutable process inputs; arguments are individual values and never shell text.</summary>
public sealed class ExecutableStartInfo
{
    private ExecutableStartInfo() { }

    /// <summary>Gets the executable path, resolved by the operating system's process API.</summary>
    public string ExecutablePath { get; private init; } = string.Empty;
    /// <summary>Gets the copied argument list.</summary>
    public IReadOnlyList<string> Arguments { get; private init; } = [];
    /// <summary>Gets the working directory, or null to inherit it.</summary>
    public string? WorkingDirectory { get; private init; }
    /// <summary>Gets copied environment overrides; null removes an inherited variable.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; private init; } = ReadOnlyDictionary<string, string?>.Empty;
    /// <summary>Gets optional UTF-8 input text; stdin is closed even when null.</summary>
    public string? StandardInput { get; private init; }
    /// <summary>Gets the stdout byte cap; zero drains and discards everything.</summary>
    public int MaxStandardOutputBytes { get; private init; }
    /// <summary>Gets the stderr byte cap; zero drains and discards everything.</summary>
    public int MaxStandardErrorBytes { get; private init; }
    /// <summary>Gets the lifecycle timeout, or null for no timeout; zero prevents launch.</summary>
    public TimeSpan? Timeout { get; private init; }

    /// <summary>Copies process inputs and validates capture limits, timeout and argument boundaries.</summary>
    /// <param name="executablePath">The path; OS launch failures become failed-start results.</param>
    /// <param name="arguments">Individual arguments; null means no arguments.</param>
    /// <param name="workingDirectory">Optional working directory.</param>
    /// <param name="environment">Overrides and removals, copied with ordinal keys.</param>
    /// <param name="standardInput">Optional UTF-8 input text.</param>
    /// <param name="maxStandardOutputBytes">Maximum stdout bytes, default 1 MiB.</param>
    /// <param name="maxStandardErrorBytes">Maximum stderr bytes, default 1 MiB.</param>
    /// <param name="timeout">Optional timeout from zero through the platform timer's maximum.</param>
    /// <returns>Immutable launch information.</returns>
    /// <exception cref="ArgumentNullException">The path or an argument is null.</exception>
    /// <exception cref="ArgumentException">An argument contains NUL.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit is negative or timeout is outside the timer range.</exception>
    public static ExecutableStartInfo Create(
        string executablePath, IReadOnlyList<string>? arguments = null, string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null, string? standardInput = null,
        int maxStandardOutputBytes = 1024 * 1024, int maxStandardErrorBytes = 1024 * 1024,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(executablePath);
        ArgumentOutOfRangeException.ThrowIfNegative(maxStandardOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxStandardErrorBytes);
        if (timeout is { } duration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero, nameof(timeout));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(duration.TotalMilliseconds, uint.MaxValue - 1D, nameof(timeout));
        }
        var copiedArguments = new string[arguments?.Count ?? 0];
        for (var i = 0; i < copiedArguments.Length; i++)
        {
            var argument = arguments![i];
            ArgumentNullException.ThrowIfNull(argument, nameof(arguments));
            if (argument.Contains('\0'))
            {
                throw new ArgumentException(ErrorMessages.InvalidArgument, nameof(arguments));
            }
            copiedArguments[i] = argument;
        }
        var copiedEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                copiedEnvironment.Add(pair.Key, pair.Value);
            }
        }
        return new ExecutableStartInfo
        {
            ExecutablePath = executablePath, Arguments = Array.AsReadOnly(copiedArguments),
            WorkingDirectory = workingDirectory, Environment = new ReadOnlyDictionary<string, string?>(copiedEnvironment),
            StandardInput = standardInput, MaxStandardOutputBytes = maxStandardOutputBytes,
            MaxStandardErrorBytes = maxStandardErrorBytes, Timeout = timeout
        };
    }
}
