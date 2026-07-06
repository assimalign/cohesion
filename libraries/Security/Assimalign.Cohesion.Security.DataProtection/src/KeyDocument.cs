using System;

namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// An opaque, persisted representation of a single ring key: a repository-friendly
/// <see cref="Name"/> and the serialized key <see cref="Content"/>. The content is opaque to
/// the repository — only the key ring understands its layout — so a repository implementation
/// (file system, and later a secret store) is a pure blob store and need not know anything
/// about key material or lifecycle.
/// </summary>
public sealed class KeyDocument
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KeyDocument"/> class.
    /// </summary>
    /// <param name="name">
    /// A stable, unique, storage-safe name for the document (the key ring uses the key id).
    /// Must be non-empty and contain no directory-separator or path-traversal characters.
    /// </param>
    /// <param name="content">The opaque serialized key bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or empty.</exception>
    public KeyDocument(string name, ReadOnlyMemory<byte> content)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("A key document name must be a non-empty string.", nameof(name));
        }

        Name = name;
        Content = content;
    }

    /// <summary>
    /// Gets the stable, unique name identifying this document within its repository.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the opaque serialized key bytes.
    /// </summary>
    public ReadOnlyMemory<byte> Content { get; }
}
