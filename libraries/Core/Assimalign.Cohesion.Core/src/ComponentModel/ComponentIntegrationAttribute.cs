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
/// When <see cref="FactoryMethodName"/> identifies public static methods, the generator copies
/// each supported overload's signature and emits
/// <c>receiver.TargetMethodName&lt;Contract&gt;(FactoryType.FactoryMethodName(args)); return receiver;</c>.
/// The adapter forwards every argument without adding behavior of its own.
/// </para>
/// <para>
/// When <see cref="FactoryMethodName"/> identifies a public instance method on a publicly
/// constructible builder type, the generator projects it as
/// <c>Verb(Action&lt;FactoryType&gt;)</c>. The emitted adapter constructs the builder, invokes the
/// configuration action, calls the instance method, and registers the resulting component through
/// a producer.
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
    /// <param name="factoryType">The public static factory type or publicly constructible builder type in the declaring assembly.</param>
    /// <param name="factoryMethodName">The name of either a public static factory method, whose supported overloads are copied, or a public zero-parameter instance method on a builder, which is projected as <c>Verb(Action&lt;FactoryType&gt;)</c> using the construct-configure-build template.</param>
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
