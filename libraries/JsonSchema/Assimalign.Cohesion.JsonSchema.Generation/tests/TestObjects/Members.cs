using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.JsonSchema.Generation.Tests;

public struct Point
{
    public int X { get; set; }

    public int Y { get; set; }
}

/// <summary>Uses a struct as a value and as a <see cref="Nullable{T}"/>.</summary>
public sealed class Segment
{
    public Point Start { get; set; }

    public Point? End { get; set; }
}

/// <summary>Constructor parameters with defaults, of a type used twice.</summary>
public sealed record Route(Point From = default, Point To = default);

public sealed class Money
{
    public Money(decimal amount)
    {
        Amount = amount;
    }

    public decimal Amount { get; }
}

/// <summary>Writes <see cref="Money"/> as a string, which its contract does not describe.</summary>
public sealed class MoneyConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(decimal.Parse(reader.GetString()!, CultureInfo.InvariantCulture));

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Amount.ToString(CultureInfo.InvariantCulture));
}

public sealed class Invoice
{
    [JsonConverter(typeof(MoneyConverter))]
    public Money Total { get; set; } = new(0);

    public Money? Tax { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Strict(string Name, int Count = 1);

public sealed class Flexible
{
    public string Name { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class Primitives
{
    public byte[] Bytes { get; set; } = [];

    public DateTime When { get; set; }

    public DateOnly Day { get; set; }

    public TimeSpan Duration { get; set; }

    public Uri? Link { get; set; }

    public char Initial { get; set; }

    public long Big { get; set; }

    public double Ratio { get; set; }

    public object? Anything { get; set; }

    public JsonElement Raw { get; set; }

    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public int Quoted { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    public double Measured { get; set; }
}
