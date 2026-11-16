using System;
using System.IO;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Internal;

// Deviates from the repo RootNamespace rule: model-local shared engine composition.
namespace Assimalign.Cohesion.Database;

internal static class DatabaseRecoveryPreflight
{
    internal static bool Required(string? directory, DatabaseMemoryFiles? memory, string name, string set,
        CancellationToken cancellationToken = default)
    {
        // This is only an optimization. Let the normal ordered gates diagnose unreadable
        // sources; a data-journal precheck must never mask an earlier catalog refusal.
        try
        {
            if (directory is not null)
            {
                var files = DatabaseFileNames.GetFiles(directory, set);
                using var journal = new StorageStream(new FileStream(files.Journal, FileMode.Open, FileAccess.Read, FileShare.Read));
                return StorageRecoveryJournal.RequiresRecovery(journal, new FileInfo(files.Data).Length, cancellationToken);
            }
            if (memory is null || !memory.TryOpen(name, set, out var data, out var log, out var backup, readOnly: true)) { return true; }
            using (data) using (log) using (backup)
            {
                return StorageRecoveryJournal.RequiresRecovery(log, data.Length, cancellationToken);
            }
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    internal static void AssertRecovered(Storage.Storage storage)
    {
        if (storage.Data.CanWrite) { throw new InvalidOperationException(EngineErrorMessages.RecoveryProbeRequired); }
    }
}
