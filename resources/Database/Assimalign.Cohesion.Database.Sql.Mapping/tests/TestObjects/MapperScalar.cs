using System;

namespace Assimalign.Cohesion.Database.Sql.Mapping.Tests;

internal sealed class MapperScalar
{
    public int Id { get; set; }
    public bool Boolean { get; set; }
    public byte Byte { get; set; }
    public sbyte Int8 { get; set; }
    public short Int16 { get; set; }
    public int Int32 { get; set; }
    public long Int64 { get; set; }
    public float Float32 { get; set; }
    public double Float64 { get; set; }
    public decimal Decimal { get; set; }
    public string? Text { get; set; }
    public byte[]? Binary { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly Time { get; set; }
    public DateTime Timestamp { get; set; }
    public DateTimeOffset Offset { get; set; }
    public TimeSpan Duration { get; set; }
    public Guid Guid { get; set; }
}
