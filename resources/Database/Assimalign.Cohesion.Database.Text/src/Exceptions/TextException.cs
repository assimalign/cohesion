using System;

namespace Assimalign.Cohesion.Database.Text;

/// <summary>A structured failure owned by the independent Text child root.</summary>
public sealed class TextException : Exception
{
    internal TextException(TextErrorCode code, string message) : base(message)
    {
        Code = code;
    }

    /// <summary>Gets the code a model can translate to its own diagnostic vocabulary.</summary>
    public TextErrorCode Code { get; }
}
