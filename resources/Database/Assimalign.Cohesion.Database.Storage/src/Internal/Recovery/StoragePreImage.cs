namespace Assimalign.Cohesion.Database.Storage.Internal;

/// <summary>
/// A page's pre-image as a storage transaction holds it: its full image encoding in memory
/// (<see cref="PageImageCodec.EncodeImage"/>), or, when it was spilled past the storage's
/// pre-image budget, where the full page image record that carries it lies in the journal; and
/// the LSN of the page's last record when the transaction first touched it.
/// </summary>
/// <param name="Runs">The encoded image, or null when spilled.</param>
/// <param name="Location">The full page image record, when spilled.</param>
/// <param name="BaseLsn">
/// The LSN of the page's last record once the transaction touched it: its full page image when
/// the touch journaled one, otherwise the record that last changed it (or the LSN recovery
/// stamped). The pre-image is the page as recovery rebuilds it at this LSN, so the commit's
/// delta names it as its base and a rollback restores it with the content.
/// </param>
internal readonly record struct StoragePreImage(byte[]? Runs, StorageJournalLocation Location, long BaseLsn)
{
    /// <summary>
    /// Gets whether the pre-image lives in the journal rather than in memory.
    /// </summary>
    public bool IsSpilled => Runs is null;
}
