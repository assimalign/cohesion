using System;
using System.IO;

namespace Assimalign.Cohesion.Connections.Tcp.Tests;

/// <summary>
/// Produces a short, process-unique Unix domain socket path under the system temp directory.
/// </summary>
/// <remarks>
/// The path is kept short (a trimmed GUID) because Unix domain socket paths are capped at roughly
/// 104&#8211;108 bytes across platforms; a long temp directory plus a full GUID can overflow that limit
/// on macOS.
/// </remarks>
internal static class UnixSocketPath
{
    public static string Create()
        => Path.Combine(Path.GetTempPath(), $"ch{Guid.NewGuid():N}"[..14] + ".sock");
}
