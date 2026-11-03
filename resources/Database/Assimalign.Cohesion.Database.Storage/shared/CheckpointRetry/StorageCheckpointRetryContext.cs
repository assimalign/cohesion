using System;

// Deviates from the repo RootNamespace rule per shared-source design: this stateless
// exception-context contract is linked into Storage and Transactions. The predicate
// and its captured bracket state are created only in Storage, outside this module.
namespace Assimalign.Cohesion.Database.Storage.Internal;

internal static class StorageCheckpointRetryContext
{
    private const string _blockedKey = "Assimalign.Cohesion.Database.Storage.CheckpointBlocked";

    internal static void Attach(Exception exception, Func<bool> isBlocked)
        => exception.Data[_blockedKey] = isBlocked;

    internal static Func<bool>? Read(Exception exception)
        => exception.Data[_blockedKey] as Func<bool>;
}
