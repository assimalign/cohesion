using System;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Reports an actionable refusal of a resource command.</summary>
public class ResourceCommandRejectedException : Exception
{
    /// <summary>Creates a command refusal.</summary>
    /// <param name="detail">The provider's named, actionable reason.</param>
    /// <exception cref="ArgumentException">The detail is blank.</exception>
    public ResourceCommandRejectedException(string detail) : base(detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        Detail = detail;
    }

    /// <summary>Gets the provider's refusal detail.</summary>
    public string Detail { get; }
}
