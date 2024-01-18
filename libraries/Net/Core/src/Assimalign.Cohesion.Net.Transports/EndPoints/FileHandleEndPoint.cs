
using System;
using System.Net;

namespace Assimalign.Cohesion.Net.Transports;

public class FileHandleEndPoint : EndPoint
{
    /// <summary>
    /// Initializes a new instance of <see cref="FileHandleEndPoint"/>.
    /// </summary>
    /// <param name="fileHandle">The file handle.</param>
    /// <param name="fileHandleType">The file handle type.</param>
    public FileHandleEndPoint(ulong fileHandle, FileHandleType fileHandleType)
    {
        FileHandle = fileHandle;
        FileHandleType = fileHandleType;

        switch (fileHandleType)
        {
            case FileHandleType.Auto:
            case FileHandleType.Tcp:
            case FileHandleType.Pipe:
                break;
            default:
                throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Gets the file handle.
    /// </summary>
    public ulong FileHandle { get; }

    /// <summary>
    /// Gets the file handle type.
    /// </summary>
    public FileHandleType FileHandleType { get; }
}
