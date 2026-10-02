using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// Throw members for <see cref="FileSystemException"/>, declared as static extension members so a
/// provider calls them on the exception type: <c>FileSystemException.ThrowPathOutsideRoot(path)</c>.
/// </summary>
/// <remarks>
/// Reusable throw logic lives in extension members rather than on the exception type. The older
/// <c>Throw*</c> members still declared on <see cref="FileSystemException"/> predate that rule; they move
/// here when they are next changed, and their callers do not change.
/// </remarks>
public static class FileSystemExceptionExtensions
{
    extension(FileSystemException)
    {
        /// <summary>
        /// Throws a <see cref="FileSystemException"/> with <see cref="FileSystemErrorCode.PathOutsideRoot"/>
        /// indicating that <paramref name="path"/> resolves outside the file system's root directory.
        /// </summary>
        /// <remarks>
        /// The message names only the caller's path. It deliberately leaves out the root and the
        /// resolved host location, which would disclose the host's directory layout to whoever sees
        /// the message.
        /// </remarks>
        /// <param name="path">The path as the caller supplied it.</param>
        /// <param name="innerException">Optional inner exception.</param>
        /// <exception cref="FileSystemException">Always.</exception>
        [DoesNotReturn]
        public static void ThrowPathOutsideRoot(FileSystemPath path, Exception? innerException = null)
        {
            throw new FileSystemException(
                FileSystemErrorCode.PathOutsideRoot,
                $"The path resolves outside the file system root: '{path}'.",
                innerException);
        }
    }
}
