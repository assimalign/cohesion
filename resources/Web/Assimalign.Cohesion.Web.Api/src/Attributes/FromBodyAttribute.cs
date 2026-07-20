using System;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Declares that a typed endpoint-handler parameter is bound by deserializing the request body
/// through the registered content-serialization registry (<c>Assimalign.Cohesion.Web.Serialization</c>).
/// </summary>
/// <remarks>
/// This attribute is <b>compile-time input to the Cohesion Web source generator</b>
/// (<c>Assimalign.Cohesion.SourceGeneration.Web</c>): the generator reads it while rewriting a
/// typed <c>Map*</c> call site into an AOT-safe binding thunk. It is never reflected over at run
/// time, in keeping with the repository's metadata-carrier discipline. At most one parameter per
/// handler may bind from the body, and body binding is mutually exclusive with form binding.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class FromBodyAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FromBodyAttribute"/> class.
    /// </summary>
    public FromBodyAttribute()
    {
    }
}
