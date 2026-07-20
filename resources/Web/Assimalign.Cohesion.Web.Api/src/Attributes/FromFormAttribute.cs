using System;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Declares that a typed endpoint-handler parameter is bound from a submitted form field
/// (<c>application/x-www-form-urlencoded</c> or <c>multipart/form-data</c>, via
/// <c>Assimalign.Cohesion.Http.Forms</c>).
/// </summary>
/// <remarks>
/// This attribute is <b>compile-time input to the Cohesion Web source generator</b>
/// (<c>Assimalign.Cohesion.SourceGeneration.Web</c>): the generator reads it while rewriting a
/// typed <c>Map*</c> call site into an AOT-safe binding thunk. It is never reflected over at run
/// time, in keeping with the repository's metadata-carrier discipline. Form binding is mutually
/// exclusive with body binding on the same handler.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class FromFormAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FromFormAttribute"/> class.
    /// </summary>
    public FromFormAttribute()
    {
    }

    /// <summary>
    /// Gets or sets the form field name to bind from. When <see langword="null"/> or empty, the
    /// parameter's own name is used.
    /// </summary>
    public string? Name { get; set; }
}
