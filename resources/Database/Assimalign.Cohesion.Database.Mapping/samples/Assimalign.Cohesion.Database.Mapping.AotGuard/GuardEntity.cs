namespace Assimalign.Cohesion.Database.Mapping.AotGuard;

/// <summary>Represents the statically mapped entity exercised by the NativeAOT guard.</summary>
public sealed class GuardEntity
{
    /// <summary>Gets or sets the immutable tracked identity.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the mapped scalar value.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the mapped binary value.</summary>
    public byte[] Payload { get; set; } = [];
}
