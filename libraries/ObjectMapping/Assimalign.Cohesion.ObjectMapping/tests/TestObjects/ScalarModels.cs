namespace Assimalign.Cohesion.ObjectMapping.Tests;

/// <summary>Source POCO for scalar member-to-member mapping tests.</summary>
public sealed class PersonSource
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? MiddleName { get; set; }
    public int Age { get; set; }
}

/// <summary>Target POCO for scalar member-to-member mapping tests.</summary>
public sealed class PersonTarget
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? MiddleName { get; set; }
    public int Age { get; set; }
    public int? NullableAge { get; set; }
    public object? Tag { get; set; }
}

/// <summary>Source carrying only name fields, used for multi-source composition.</summary>
public sealed class NameSource
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
}

/// <summary>Source carrying only an age, used for multi-source composition.</summary>
public sealed class AgeSource
{
    public int Age { get; set; }
}
