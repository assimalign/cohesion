using System;
using System.IO;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Hosting;

public class HostEnvironment : IHostEnvironment
{
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
    public FileSystemPath? ContentRootPath { get; init; } 
}
