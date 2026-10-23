using System;
using System.Threading;

// Deviates from namespace-matches-assembly: this model-local helper is compiled
// from the Database root's shared source into each model assembly.
namespace Assimalign.Cohesion.Database;

/// <summary>
/// The one copy of the forget every model engine runs from its
/// <see cref="DatabaseEngine.ForgetClosedDatabaseCore"/> (owner decision 33 of 2026-10-06, #1289;
/// database-area.md rule 8): a database whose close ended leaves the engine's registry unless the
/// engine already let it go.
/// </summary>
/// <remarks>
/// <para>
/// Each engine guards its registry with one lock and publishes the databases it tracks as a
/// lock-free snapshot, rebuilt under the lock whenever the registry changes. The engine disposes a
/// database it let go (a drop, a reopen after going offline) while holding that lock, after it
/// removed the database and rebuilt the snapshot, and that disposal waits for a close a holder
/// started (<see cref="DatabaseInstance.Dispose"/>). A forget that waited for the lock there would
/// deadlock with it, so the forget reads the snapshot first and returns at once when the database
/// is not in it; it takes the lock only for a bounded wait, re-reading the snapshot between
/// attempts, and removes the database only when the registry still holds that very instance.
/// </para>
/// <para>
/// Compiled into each model assembly from the root's <c>shared</c> folder, as the engines'
/// checkpointer is: the registry is each leaf's own state, so the routine cannot live on the root
/// base without a registry type the base does not need.
/// </para>
/// </remarks>
internal static class DatabaseRegistry
{
    // How long one attempt waits for the engine's lock before the snapshot is read again.
    private static readonly TimeSpan _lockAttempt = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Removes a database whose close ended from an engine's registry, when the registry still
    /// holds that instance.
    /// </summary>
    /// <typeparam name="TDatabase">The model's database type.</typeparam>
    /// <param name="database">The database whose close ended.</param>
    /// <param name="sync">The lock that guards the engine's registry.</param>
    /// <param name="published">Reads the engine's published, lock-free snapshot of the databases it tracks.</param>
    /// <param name="removeLocked">
    /// Under the lock: removes the database when the registry holds that instance under its name,
    /// and rebuilds the published snapshot.
    /// </param>
    internal static void Forget<TDatabase>(TDatabase database, object sync, Func<TDatabase[]> published, Action<TDatabase> removeLocked)
        where TDatabase : DatabaseInstance
    {
        while (Array.IndexOf(published(), database) >= 0)
        {
            if (!Monitor.TryEnter(sync, _lockAttempt))
            {
                continue;
            }

            try
            {
                removeLocked(database);
                return;
            }
            finally
            {
                Monitor.Exit(sync);
            }
        }
    }
}
