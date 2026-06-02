namespace Assimalign.Cohesion.ObjectMapping.Tests;

/// <summary>Flat source mapped into a nested target (mirrors the requested scenario).</summary>
public sealed class Test1
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
}

/// <summary>Target whose data lives under a nested <see cref="Test3Info"/> member.</summary>
public sealed class Test3
{
    public Test3Info? Info { get; set; }
}

/// <summary>Nested target member type (created on demand when mapping <c>t =&gt; t.Info.FirstName</c>).</summary>
public sealed class Test3Info
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
}

/// <summary>Source for the no-parameterless-constructor negative case.</summary>
public sealed class NoCtorSource
{
    public string? Name { get; set; }
}

/// <summary>Target whose intermediate member cannot be created on demand.</summary>
public sealed class NoCtorTarget
{
    public NoCtorIntermediate? Nested { get; set; }
}

/// <summary>Intermediate type with no public parameterless constructor.</summary>
public sealed class NoCtorIntermediate
{
    public NoCtorIntermediate(int value) => Value = value;

    public int Value { get; }

    public string? Name { get; set; }
}
