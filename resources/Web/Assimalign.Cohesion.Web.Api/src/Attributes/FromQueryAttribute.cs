using System;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Declares that a typed endpoint-handler parameter is bound from the request query string.
/// </summary>
/// <remarks>
/// This attribute is <b>compile-time input to the Cohesion Web source generator</b>
/// (<c>Assimalign.Cohesion.SourceGeneration.Web</c>): the generator reads it while rewriting a
/// typed <c>Map*</c> call site into an AOT-safe binding thunk. It is never reflected over at run
/// time, in keeping with the repository's metadata-carrier discipline.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class FromQueryAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FromQueryAttribute"/> class.
    /// </summary>
    public FromQueryAttribute()
    {
    }

    /// <summary>
    /// Gets or sets the query key to bind from. When <see langword="null"/> or empty, the
    /// parameter's own name is used.
    /// </summary>
    public string? Name { get; set; }
}
