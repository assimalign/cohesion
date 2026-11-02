// Deviates from the repo RootNamespace rule: stateless recovery readers are compiled
// into Storage and each engine without exposing a public implementation bridge.
namespace Assimalign.Cohesion.Database.Storage.Internal;

// Ordinal preserves file order even for a malformed journal whose verified LSNs go backwards.
internal readonly record struct StorageRecoveryPagePlan(long Ordinal, long LastLsn, bool Committed, int RecordCount = 1);
