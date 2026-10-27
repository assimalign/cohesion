namespace Assimalign.Cohesion.Database.Documents;

/// <summary>Classifies a registered document function.</summary>
public enum DocumentFunctionKind : byte
{
    /// <summary>Computes a value from one row.</summary>
    Scalar,
    /// <summary>Computes a value from a group of rows.</summary>
    Aggregate,
}
