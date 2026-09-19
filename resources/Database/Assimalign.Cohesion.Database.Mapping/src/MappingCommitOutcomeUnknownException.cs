using System;

namespace Assimalign.Cohesion.Database.Mapping;

/// <summary>Reports that a store may have committed a save but cannot confirm its outcome.</summary>
/// <remarks>The unit of work becomes unusable. Do not replay the save in another scope until
/// authoritative reconciliation establishes whether it committed. This exception never means rollback.</remarks>
public sealed class MappingCommitOutcomeUnknownException : Exception
{
    /// <summary>Creates an unresolved commit failure with its underlying cause.</summary>
    /// <param name="message">The store-specific explanation of the unresolved outcome.</param>
    /// <param name="innerException">The failure that prevented a definite outcome.</param>
    public MappingCommitOutcomeUnknownException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
