using System;
using System.Reflection;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// Test access to the journal's append buffer (#1252) from the test assemblies that link these
/// test objects. The buffer's size limit is internal to the storage and visible to its own tests
/// only, so this helper reaches it by reflection: test code, never trimmed or compiled ahead of
/// time.
/// </summary>
public static class JournalBufferHooks
{
    /// <summary>
    /// The smallest append buffer this helper sets: one frame without payload (38 bytes) fits, two
    /// do not. Every append then drains the frame buffered ahead of it, and a page image is written
    /// directly, so each append reaches the medium about where it did before #1252 and a test can
    /// fail one.
    /// </summary>
    public const int SmallestBuffer = 64;

    private static readonly PropertyInfo MaximumBufferBytes =
        typeof(StorageJournal).GetProperty("MaximumBufferBytes", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("StorageJournal.MaximumBufferBytes was renamed; update the test hook.");

    /// <summary>
    /// Sets the size the journal's append buffer may grow to.
    /// </summary>
    /// <param name="journal">A storage journal.</param>
    /// <param name="bytes">The size, at least one frame without payload.</param>
    public static void SetMaximumBufferBytes(IStorageJournal journal, int bytes)
        => MaximumBufferBytes.SetValue((StorageJournal)journal, bytes);
}
