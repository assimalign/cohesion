using System.Collections.Generic;

namespace Assimalign.Cohesion.Content;

/// <summary>
/// An immutable descriptor identifying a content format: its name, classification, media types, file
/// extensions, and governing specification. Format packages expose a single shared descriptor for the
/// format they implement.
/// </summary>
/// <remarks>
/// The descriptor is metadata only — it carries no parsing or serialization behavior. Services use it
/// to classify, route, and negotiate content without referencing format packages directly.
/// </remarks>
public sealed class ContentFormat
{
    /// <summary>Gets the short, human-readable name of the format, for example <c>YAML</c> or <c>BMFF</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the broad classification of content described by the format.</summary>
    public ContentKind Kind { get; init; } = ContentKind.Unknown;

    /// <summary>Gets the media types associated with the format, most specific first.</summary>
    public IReadOnlyList<string> MediaTypes { get; init; } = [];

    /// <summary>Gets the file extensions (including the leading dot) associated with the format.</summary>
    public IReadOnlyList<string> FileExtensions { get; init; } = [];

    /// <summary>Gets a reference to the governing specification, such as an RFC or specification URL, when the format is standards-driven.</summary>
    public string? Specification { get; init; }

    /// <summary>Gets the shared descriptor for content whose format is not known.</summary>
    public static ContentFormat Unknown { get; } = new() { Name = "unknown" };

    /// <inheritdoc/>
    public override string ToString() => Name;
}
