namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>The scalar storage targets supported by GQL CAST.</summary>
public enum GqlCastType
{
    /// <summary>The Int32 target.</summary>
    Int32,
    /// <summary>The Int64 target.</summary>
    Int64,
    /// <summary>The Float32 target.</summary>
    Float32,
    /// <summary>The Float64 target.</summary>
    Float64,
    /// <summary>The Decimal target.</summary>
    Decimal,
    /// <summary>The String target.</summary>
    String,
    /// <summary>The Boolean target.</summary>
    Boolean,
    /// <summary>The Date target.</summary>
    Date,
    /// <summary>The Timestamp target.</summary>
    Timestamp,
}
