using System;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// 
/// </summary>
public readonly struct HostEvent
{

    public HostEvent(string name, string? category = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        Name = name;
        Category = category;
    }

    /// <summary>
    /// Returns the name of the event.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The category of the event. 
    /// <br/> 
    /// <br/> 
    /// Categories: <i> debug, information, warning, error, critical </i>
    /// </summary>
    public string? Category { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        return Name;
    }
}
