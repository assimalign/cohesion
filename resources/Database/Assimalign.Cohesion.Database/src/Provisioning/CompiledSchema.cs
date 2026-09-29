using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The model-agnostic identity of a compiled schema. Each model package derives a type that
/// carries that model's object shape; the root never learns what those shapes are.
/// </summary>
public abstract class CompiledSchema
{
    /// <summary>Initializes the identity of a compiled schema.</summary>
    /// <param name="format">The schema document format identifier.</param>
    /// <param name="name">The database name this schema targets.</param>
    /// <param name="model">The engine model this schema is written for.</param>
    /// <param name="allowsDestructiveChanges">Whether applying the schema may perform destructive operations.</param>
    protected CompiledSchema(string format, string name, EngineModel model, bool allowsDestructiveChanges)
    {
        Format = format;
        Name = name;
        Model = model;
        AllowsDestructiveChanges = allowsDestructiveChanges;
    }

    /// <summary>The schema document format identifier.</summary>
    public string Format { get; }

    /// <summary>The database name this schema targets.</summary>
    public string Name { get; }

    /// <summary>The engine model this schema is written for.</summary>
    public EngineModel Model { get; }

    /// <summary>Whether applying this schema may perform destructive operations.</summary>
    public bool AllowsDestructiveChanges { get; }

    /// <summary>
    /// The stable, canonical serialization of this schema. Two schemas with the same canonical
    /// document are the same schema; this is what <see cref="Hash"/> is computed over and what
    /// the catalog records for drift detection.
    /// </summary>
    [JsonIgnore]
    public abstract string CanonicalDocument { get; }

    /// <summary>Content hash of <see cref="CanonicalDocument"/>.</summary>
    [JsonIgnore]
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalDocument)));
}
