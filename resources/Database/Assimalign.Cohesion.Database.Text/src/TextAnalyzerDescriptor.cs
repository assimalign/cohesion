using System;

namespace Assimalign.Cohesion.Database.Text;

/// <summary>The complete analyzer identity a caller persists alongside analyzed data.</summary>
/// <remarks>Persist all three properties. Any tokenizer, Unicode, stop-list or stemmer
/// change requires a version increase and index refusal or rebuild by the owning model.</remarks>
public sealed class TextAnalyzerDescriptor : IEquatable<TextAnalyzerDescriptor>
{
    private TextAnalyzerDescriptor(string name, int version, bool foldAccents)
    {
        Name = name;
        Version = version;
        FoldAccents = foldAccents;
    }

    /// <summary>Gets the default simple analyzer identity, version 1, preserving accents.</summary>
    public static TextAnalyzerDescriptor Simple { get; } = new("simple", 1, false);

    /// <summary>Gets the English analyzer identity, version 1, preserving accents.</summary>
    public static TextAnalyzerDescriptor English { get; } = new("english", 1, false);

    /// <summary>Gets the exact, case-sensitive persisted name.</summary>
    public string Name { get; }

    /// <summary>Gets the persisted algorithm version.</summary>
    public int Version { get; }

    /// <summary>Gets whether canonical accent removal precedes case folding.</summary>
    public bool FoldAccents { get; }

    /// <summary>Reconstitutes a persisted identity without silently upgrading it.</summary>
    /// <param name="name">The persisted name, including names unavailable to this implementation.</param>
    /// <param name="version">The positive persisted version.</param>
    /// <param name="foldAccents">The persisted accent-folding choice; the default is false.</param>
    /// <returns>An immutable identity; <see cref="TextAnalyzer.Create"/> verifies availability.</returns>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The version is not positive.</exception>
    public static TextAnalyzerDescriptor FromPersisted(string name, int version, bool foldAccents = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        return new TextAnalyzerDescriptor(name, version, foldAccents);
    }

    /// <inheritdoc />
    public bool Equals(TextAnalyzerDescriptor? other) => other is not null
        && Name == other.Name && Version == other.Version && FoldAccents == other.FoldAccents;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TextAnalyzerDescriptor other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Name, Version, FoldAccents);
}
