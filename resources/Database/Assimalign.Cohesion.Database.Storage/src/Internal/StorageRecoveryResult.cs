namespace Assimalign.Cohesion.Database.Storage.Internal;

/// <summary>
/// What recovery found and did (<see cref="StorageRecovery.Run"/>).
/// </summary>
/// <param name="MaxSequence">The highest transaction sequence in the journal; zero when it is empty.</param>
/// <param name="CheckpointLsn">The LSN of the checkpoint record the journal starts with; zero when it has none.</param>
/// <param name="PagesRebuilt">The number of pages the replay rebuilt and wrote.</param>
internal readonly record struct StorageRecoveryResult(long MaxSequence, long CheckpointLsn, int PagesRebuilt);
