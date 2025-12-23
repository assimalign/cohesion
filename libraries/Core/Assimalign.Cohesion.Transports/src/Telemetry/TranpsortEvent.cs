namespace Assimalign.Cohesion.Transports;

public readonly struct TranpsortEvent
{
    public TranpsortEvent(string name, string? category = null)
    {
        Name = name;
        Category = category;
    }

    /// <summary>
    /// The name of the event.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The name of the category.
    /// </summary>
    public string? Category { get; }
}
