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
    /// Reserved overflow layout with a body length given by <see cref="Units.Page.OverflowSize"/>.
    /// The header and body must fit one standard page; multi-page overflow storage is not implemented.
    /// </summary>
    Overflow = 1,

    /// <summary>
    /// The page body is encrypted at rest (reserved for the encryption-at-rest feature;
    /// the nonce and MAC live in the page header).
    /// </summary>
    Encrypted = 2,
}
