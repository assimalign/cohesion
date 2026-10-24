namespace Assimalign.Cohesion.Database.Documents;

/// <summary>Controls whether NULL and MISSING reach a document function core.</summary>
public enum DocumentNullBehavior : byte
{
    /// <summary>MISSING wins over NULL; neither invokes a scalar core, and both skip aggregate rows.</summary>
    PropagateMissingOrNull,
    /// <summary>The core receives every input, including NULL and MISSING.</summary>
    CalledOnNullOrMissingInput,
}
