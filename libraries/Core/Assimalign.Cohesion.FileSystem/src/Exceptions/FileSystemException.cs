using System;

namespace Assimalign.Cohesion.FileSystem;

public class FileSystemException : CohesionException
{
    public FileSystemException(string message) 
        : base(message)
    {
    }

    public FileSystemException(string message, Exception innerException) 
        : base(message, innerException)
    {
    }
}
