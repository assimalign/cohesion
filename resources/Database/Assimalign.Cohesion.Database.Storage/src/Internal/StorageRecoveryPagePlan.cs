namespace Assimalign.Cohesion.Database.Storage.Internal;

// Ordinal preserves file order even for a malformed journal whose verified LSNs go backwards.
internal readonly record struct StorageRecoveryPagePlan(long Ordinal, long LastLsn, bool Committed);
