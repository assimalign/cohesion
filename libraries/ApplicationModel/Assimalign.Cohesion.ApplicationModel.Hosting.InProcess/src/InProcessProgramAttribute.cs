using System;
using System.ComponentModel;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;

/// <summary>
/// Marks a resource program's assembly as one a gateway can run in process: it names the public
/// entry point the program's build generated, which a gateway program that references the program
/// binds to the resource.
/// </summary>
/// <remarks>
/// <para>
/// The Cohesion source generator applies it, with the entry point it generates, to an executable
/// that references this package and not the gateway: a top-level program compiles to a type and
/// method nothing outside its assembly can name, so the program's own build exposes it. Application
/// code does not apply it.
/// </para>
/// <para>
/// The entry point is a public static <c>RunAsync(string[] args)</c> method returning a task that
/// completes when the program ends, as an asynchronous <c>Main</c> returns when its host has stopped.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class InProcessProgramAttribute : Attribute
{
    /// <summary>
    /// Names the program's entry point, with no provider: the gateway program supplies it.
    /// </summary>
    /// <param name="program">The type that declares the program's <c>RunAsync</c> entry point.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    public InProcessProgramAttribute(Type program)
    {
        ArgumentNullException.ThrowIfNull(program);
        Program = program;
    }

    /// <summary>
    /// Names the program's entry point and the provider of the resource it runs.
    /// </summary>
    /// <param name="program">The type that declares the program's <c>RunAsync</c> entry point.</param>
    /// <param name="provider">The provider of the resource the program runs, such as <c>Web</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    public InProcessProgramAttribute(Type program, string? provider)
        : this(program)
    {
        Provider = provider;
    }

    /// <summary>
    /// Gets the type that declares the program's <c>RunAsync</c> entry point.
    /// </summary>
    public Type Program { get; }

    /// <summary>
    /// Gets the provider of the resource the program runs, such as <c>Web</c>, or
    /// <see langword="null"/> when the program's build could not tell it.
    /// </summary>
    public string? Provider { get; }
}
