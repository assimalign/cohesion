namespace Assimalign.Cohesion.Database.Indexing.Internal;

/// <summary>
/// How many tiebreaker attributes follow a key in a <see cref="BTreeSearchKey"/>: a
/// separator keeps only the ones it needs (suffix truncation), and a search names
/// only the ones it knows. A missing attribute reads as minus infinity.
/// </summary>
/// <remarks>
/// The values 0–2 are persisted in the top two bits of an internal entry's length
/// field (<see cref="BTreeNode"/>), so they are never renumbered.
/// <see cref="PlusInfinity"/> exists for searches only and is never stored.
/// </remarks>
internal enum BTreeTiebreaker : byte
{
    /// <summary>
    /// The key alone: before every entry of the key.
    /// </summary>
    None = 0,

    /// <summary>
    /// The key and an entry reference: before every entry of the key with that
    /// reference.
    /// </summary>
    Reference = 1,

    /// <summary>
    /// The key, an entry reference and a writer: one entry's full identity.
    /// </summary>
    Entry = 2,

    /// <summary>
    /// The key followed by plus infinity: after every entry of the key (a search that
    /// starts strictly after a key).
    /// </summary>
    PlusInfinity = 3,
}
