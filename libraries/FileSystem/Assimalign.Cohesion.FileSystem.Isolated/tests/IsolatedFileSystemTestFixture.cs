using System;
using System.IO.IsolatedStorage;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.FileSystem.Isolated.Tests;

/// <summary>
/// Shared helpers for the isolated test suite. Every test acquires the per-user assembly store,
/// clears any leftover entries from prior runs or failed tests, and returns a fresh
/// <see cref="IsolatedFileSystem"/> over it.
/// </summary>
internal static class IsolatedFileSystemTestFixture
{
    /// <summary>
    /// Returns a fresh <see cref="IsolatedFileSystem"/> rooted at an empty store. The backing
    /// store is forcibly cleared first so leftover state from prior tests cannot leak in.
    /// </summary>
    /// <param name="isReadOnly">Open the store in read-only mode.</param>
    /// <param name="watchPollInterval">
    /// Override <see cref="IsolatedFileSystemOptions.WatchPollInterval"/>. Defaults to
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> so the standard suite (which never
    /// exercises Watch) doesn't pay for a background timer or risk timing flakes. Watch-specific
    /// tests pass an explicit short interval.
    /// </param>
    public static IsolatedFileSystem CreateFreshFileSystem(
        bool isReadOnly = false,
        System.TimeSpan? watchPollInterval = null)
    {
        ClearUserStoreForAssembly();

        return new IsolatedFileSystem(new IsolatedFileSystemOptions
        {
            IsReadOnly = isReadOnly,
            WatchPollInterval = watchPollInterval ?? System.Threading.Timeout.InfiniteTimeSpan,
            // We do NOT set RemoveStoreOnDispose here. Tests can opt into store-removal-on-dispose
            // when they exercise that flag directly; otherwise we just clear at start.
        });
    }

    /// <summary>
    /// Recursively deletes everything inside the per-user assembly isolated store. Suppresses
    /// errors so a partially-corrupt store from a previous failed test can still be cleaned up.
    /// </summary>
    public static void ClearUserStoreForAssembly()
    {
        using var store = IsolatedStorageFile.GetUserStoreForAssembly();
        ClearStore(store, string.Empty);
    }

    private static void ClearStore(IsolatedStorageFile store, string path)
    {
        string searchRoot = string.IsNullOrEmpty(path) ? "*" : path + "/*";

        try
        {
            foreach (var fileName in store.GetFileNames(searchRoot))
            {
                string filePath = string.IsNullOrEmpty(path) ? fileName : path + "/" + fileName;
                try { store.DeleteFile(filePath); } catch { /* best effort */ }
            }

            foreach (var dirName in store.GetDirectoryNames(searchRoot))
            {
                string dirPath = string.IsNullOrEmpty(path) ? dirName : path + "/" + dirName;
                ClearStore(store, dirPath);
                try { store.DeleteDirectory(dirPath); } catch { /* best effort */ }
            }
        }
        catch
        {
            // Best-effort cleanup; some entries may be locked.
        }
    }
}
