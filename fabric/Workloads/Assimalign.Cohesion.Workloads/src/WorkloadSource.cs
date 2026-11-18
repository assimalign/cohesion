using System;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Workloads;

/// <summary>
/// What a workload runs: a program the platform builds or loads, or an image.
/// </summary>
/// <remarks>
/// A platform declares which sources it supports through
/// <see cref="WorkloadCapabilities.ProgramSource"/> and <see cref="WorkloadCapabilities.ImageSource"/>.
/// The in-process platform runs programs; container platforms run images. A source read from
/// JSON can name both or neither; <see cref="WorkloadCapabilities.Check"/> refuses it.
/// </remarks>
public sealed record WorkloadSource
{
    [JsonConstructor]
    internal WorkloadSource(string? program, string? image)
    {
        Program = program;
        Image = image;
    }

    /// <summary>
    /// Gets the program, as the AppHost names it (its project or assembly name), or <see langword="null"/>.
    /// </summary>
    public string? Program { get; }

    /// <summary>
    /// Gets the image reference, preferably pinned by digest, or <see langword="null"/>.
    /// </summary>
    public string? Image { get; }

    /// <summary>
    /// Creates a program source.
    /// </summary>
    /// <param name="program">The program's name.</param>
    /// <returns>The source.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="program"/> is <see langword="null"/>, empty, or white space.</exception>
    public static WorkloadSource FromProgram(string program)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        return new WorkloadSource(program, null);
    }

    /// <summary>
    /// Creates an image source.
    /// </summary>
    /// <param name="image">The image reference.</param>
    /// <returns>The source.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="image"/> is <see langword="null"/>, empty, or white space.</exception>
    public static WorkloadSource FromImage(string image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        return new WorkloadSource(null, image);
    }
}
