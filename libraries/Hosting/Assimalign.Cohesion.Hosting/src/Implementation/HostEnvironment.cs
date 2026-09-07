using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Assimalign.Cohesion.Hosting;

public class HostEnvironment : IHostEnvironment
{
    private FileSystemPath? _contentRootPath;

    public HostEnvironment() { }
    
    [SetsRequiredMembers]
    public HostEnvironment(string name)
    {
        Name = name;
    }

    /// <summary>
    /// The environment name.
    /// </summary>
    public required string? Name { get; init; }

    /// <summary>
    /// Gets the root directory path for content files, or null if no content root is specified.
    /// </summary>
    public FileSystemPath? ContentRootPath
    {
        get => _contentRootPath;
        init => _contentRootPath = value;
    }

    internal void SetContentRootPath(FileSystemPath contentRootPath)
    {
        _contentRootPath = contentRootPath;
    }
}
