using System;

namespace Assimalign.Cohesion;

/// <summary>
/// Declares that this assembly can contribute a component to a composition surface owned by a
/// different assembly, without referencing that assembly.
/// </summary>
/// <remarks>
/// <para>
/// The surface is identified by <em>metadata name</em> rather than by <see cref="Type"/>, which
/// is what keeps the declaration reference-free. The Cohesion component-integration source
/// generator resolves the name in each consuming compilation: when the type resolves it emits an
/// extension member forwarding to <see cref="FactoryMethodName"/>; when it does not, it emits
/// nothing.
/// </para>
/// <para>
/// The emitted adapter is always
/// <c>receiver.TargetMethodName&lt;Contract&gt;(FactoryType.FactoryMethodName(args)); return receiver;</c>.
/// The generator copies the factory method's signature, forwards its arguments, and synthesizes
/// no behaviour of its own; everything expressive belongs in the factory method body.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class ComponentIntegrationAttribute : Attribute
{
    /// <summary>The schema revision this declaration was compiled against.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Initializes a new declaration.</summary>
    /// <param name="targetTypeName">Fully qualified metadata name of the composition surface.</param>
    /// <param name="targetMethodName">The member invoked on the surface, e.g. <c>AddSingleton</c>. May be an instance member or an extension member in the surface's own namespace.</param>
    /// <param name="factoryType">A public static type in the declaring assembly holding the factory method.</param>
    /// <param name="factoryMethodName">A public static method on <paramref name="factoryType"/>. Every overload with this name is projected.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ComponentIntegrationAttribute(
        string targetTypeName,
        string targetMethodName,
        Type factoryType,
        string factoryMethodName)
    {
        TargetTypeName = targetTypeName ?? throw new ArgumentNullException(nameof(targetTypeName));
        TargetMethodName = targetMethodName ?? throw new ArgumentNullException(nameof(targetMethodName));
        FactoryType = factoryType ?? throw new ArgumentNullException(nameof(factoryType));
        FactoryMethodName = factoryMethodName ?? throw new ArgumentNullException(nameof(factoryMethodName));
    }

    /// <summary>Gets the fully qualified metadata name of the composition surface.</summary>
    public string TargetTypeName { get; }

    /// <summary>Gets the name of the member invoked on the surface.</summary>
    public string TargetMethodName { get; }

    /// <summary>Gets the type in this assembly holding the factory method.</summary>
    public Type FactoryType { get; }

    /// <summary>Gets the name of the factory method.</summary>
    public string FactoryMethodName { get; }

    /// <summary>
    /// Gets or sets the name of the generated verb. Defaults to <see cref="FactoryMethodName"/>.
    /// </summary>
    public string? Verb { get; set; }

    /// <summary>
    /// Gets or sets an explicit generic type argument for <see cref="TargetMethodName"/>.
    /// Optional; omit it to let overload resolution infer the argument.
    /// </summary>
    public Type? Contract { get; set; }
}
