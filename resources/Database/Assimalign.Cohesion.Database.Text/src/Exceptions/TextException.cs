using System;

namespace Assimalign.Cohesion.Database.Text;

/// <summary>A structured failure owned by the independent Text child root.</summary>
public sealed class TextException : Exception
{
    internal TextException(TextErrorReason reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Gets the reason a model can translate to its own diagnostic vocabulary.</summary>
    public TextErrorReason Reason { get; }
}
