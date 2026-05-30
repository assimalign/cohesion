namespace Assimalign.Cohesion.ObjectMapping.Tests;

/// <summary>Source POCO exposing public fields, used for field mapping tests.</summary>
public sealed class FieldSource
{
    public string? Name;
    public int Value;
}

/// <summary>Target POCO exposing public fields, used for field mapping tests.</summary>
public sealed class FieldTarget
{
    public string? Name;
    public int Value;
}
