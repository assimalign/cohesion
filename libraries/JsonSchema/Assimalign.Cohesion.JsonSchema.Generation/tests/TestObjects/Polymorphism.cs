using System.Collections;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.JsonSchema.Generation.Tests;

/// <summary>An abstract base whose derived types all have a discriminator, one a string and one a number.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Circle), "circle")]
[JsonDerivedType(typeof(Square), 2)]
public abstract class Shape
{
    public string? Label { get; set; }
}

public sealed class Circle : Shape
{
    public double Radius { get; set; }
}

/// <summary>A derived type that refers back to its base.</summary>
public sealed class Square : Shape
{
    public double Side { get; set; }

    public Shape? Inner { get; set; }
}

/// <summary>A concrete base with one derived type that has a discriminator and one that does not.</summary>
[JsonDerivedType(typeof(Dog))]
[JsonDerivedType(typeof(Cat), "cat")]
public class Animal
{
    public string? Name { get; set; }
}

public sealed class Dog : Animal
{
    public bool Good { get; set; }
}

public sealed class Cat : Animal
{
    public int Lives { get; set; }
}

/// <summary>A base whose derived types differ in kind: an object, and a collection without a discriminator.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextPayload), "text")]
[JsonDerivedType(typeof(NumberPayload))]
public abstract class Payload
{
}

public sealed class TextPayload : Payload
{
    public string Value { get; set; } = string.Empty;
}

public sealed class NumberPayload : Payload, IEnumerable<int>
{
    private readonly List<int> _values = new();

    public void Add(int value) => _values.Add(value);

    public IEnumerator<int> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class Envelope
{
    public Payload Body { get; set; } = new TextPayload();

    public Payload? Attachment { get; set; }
}
