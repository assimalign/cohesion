using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Represents a detected integrity violation in a storage file, such as a page
/// whose checksum does not match its content or a malformed file header.
/// </summary>
public sealed class StorageCorruptionException : StorageException
{
    /// <summary>Identifies a detected storage integrity violation.</summary>
    public const string ErrorCode = "COHDBS003";

    /// <summary>Gets the stable diagnostic code for storage corruption.</summary>
    public string Code => ErrorCode;

    /// <summary>
    /// Initializes a new <see cref="StorageCorruptionException"/> with the specified message.
    /// </summary>
    /// <param name="message">A message describing the corruption.</param>
    public StorageCorruptionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="StorageCorruptionException"/> for a specific page.
    /// </summary>
    /// <param name="pageId">The corrupted page.</param>
    /// <param name="message">A message describing the corruption.</param>
    public StorageCorruptionException(PageId pageId, string message)
        : base(message)
    {
        PageId = pageId;
    }

    /// <summary>
    /// Gets the identifier of the corrupted page, when the corruption is page-scoped.
    /// </summary>
    public PageId? PageId { get; }
}
