using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Assimalign.Cohesion.Connections.Tcp.Internal;

/// <summary>
/// Manages the filesystem lifecycle of a Unix domain socket's backing file: resolving the bindable
/// path, removing a stale file left by an unclean shutdown before <see cref="Socket.Bind(EndPoint)"/>,
/// and unlinking the file when the listener is disposed.
/// </summary>
/// <remarks>
/// A Unix domain socket bound to a filesystem path leaves a socket special file behind. If a previous
/// process crashed without unlinking it, <see cref="Socket.Bind(EndPoint)"/> fails with
/// <see cref="SocketError.AddressAlreadyInUse"/> even though nothing is listening. Removing the stale
/// file before binding restores the rebind-after-crash behavior a real server needs; unlinking on
/// disposal keeps the filesystem clean for the next bind. Linux abstract-namespace sockets (paths
/// beginning with <c>@</c> or a NUL byte) and autobind endpoints have no filesystem entry and are
/// skipped.
/// </remarks>
internal static class UnixDomainSocketFile
{
    /// <summary>
    /// Resolves the filesystem path a Unix domain socket endpoint binds to.
    /// </summary>
    /// <param name="endPoint">The endpoint to inspect.</param>
    /// <returns>
    /// The socket file path when <paramref name="endPoint"/> is a filesystem-backed
    /// <see cref="UnixDomainSocketEndPoint"/>; otherwise <see langword="null"/> (for IP endpoints,
    /// file-handle endpoints, Linux abstract-namespace sockets, and autobind endpoints — none of which
    /// have a filesystem entry to manage).
    /// </returns>
    public static string? ResolvePath(EndPoint endPoint)
    {
        if (endPoint is not UnixDomainSocketEndPoint unixEndPoint)
        {
            return null;
        }

        string path = unixEndPoint.ToString();

        // An empty path is a Linux autobind endpoint; a leading '@' (or NUL) is the abstract namespace.
        // Neither creates a filesystem entry, so there is nothing to delete or unlink.
        if (string.IsNullOrEmpty(path) || path[0] is '@' or '\0')
        {
            return null;
        }

        return path;
    }

    /// <summary>
    /// Removes a stale socket file left behind by a prior unclean shutdown so a fresh
    /// <see cref="Socket.Bind(EndPoint)"/> can succeed.
    /// </summary>
    /// <param name="path">The socket file path resolved by <see cref="ResolvePath(EndPoint)"/>.</param>
    /// <remarks>
    /// A missing file is a no-op. Any other failure (the path is a directory, or a permissions error)
    /// is swallowed so the subsequent <see cref="Socket.Bind(EndPoint)"/> surfaces the authoritative
    /// error rather than a pre-bind cleanup exception.
    /// </remarks>
    public static void DeleteStale(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // The parent directory does not exist; Bind will surface the real error.
        }
        catch (UnauthorizedAccessException)
        {
            // The path is a directory or is not writable; let Bind report the authoritative failure.
        }
        catch (IOException)
        {
            // The file is otherwise unremovable; Bind surfaces AddressAlreadyInUse if it is truly live.
        }
    }

    /// <summary>
    /// Unlinks the socket file created by a successful bind. Best effort: a file that is already gone
    /// or cannot be removed is ignored, because disposal must not throw.
    /// </summary>
    /// <param name="path">The socket file path that was bound.</param>
    public static void Unlink(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort on teardown.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort on teardown.
        }
    }
}
