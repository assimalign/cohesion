using System;
using System.IO;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Creates the file-backed state service used by local resource controllers.</summary>
// Deviates from the repo interface-first rule per owner-authorized Phase 19 decision:
// this static construction entry point returns the ILocalResourceState contract and keeps storage internal.
public static class LocalResourceState
{
    /// <summary>Creates a state service rooted at the supplied directory without performing file I/O.</summary>
    /// <param name="stateDirectory">The local state root, resolved against the current directory when relative.</param>
    /// <returns>A service that persists local resource configuration under the state root.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stateDirectory"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stateDirectory"/> is empty or invalid.</exception>
    /// <exception cref="PathTooLongException">The state directory exceeds the platform path limit.</exception>
    public static ILocalResourceState Create(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        return new LocalResourceStateStore(stateDirectory);
    }
}
