using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// State flags for a page, persisted in the page header.
/// </summary>
/// <remarks>
/// Flags are persisted on disk, so values are append-only and never renumbered.
/// </remarks>
[Flags]
public enum PageFlags : byte
{
    /// <summary>
    /// No flags set.
    /// </summary>
    None = 0,

    /// <summary>
    /// The page continues beyond the standard page size (see <see cref="Units.Page.OverflowSize"/>).
    /// </summary>
    Overflow = 1,

    /// <summary>
    /// The page body is encrypted at rest (reserved for the encryption-at-rest feature;
    /// the nonce and MAC live in the page header).
    /// </summary>
    Encrypted = 2,
}
