using System;

namespace Assimalign.Cohesion;

// Deviates from the repo RootNamespace rule: this opt-in shared module keeps its owning
// namespace in every contributing assembly. The attribute is internal to each compilation.

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
/// Selecting <see cref="ComponentIntegrationTemplate.Builder"/> for a public instance method on a
/// publicly constructible builder type projects it as
/// <c>Verb(Action&lt;FactoryType&gt;? configure = null)</c>. The emitted adapter constructs the
/// builder, invokes the configuration action when one is given (a bare call, or
/// <see langword="null"/>, builds the builder's defaults), calls the instance method at
/// registration time, and registers the resulting component through a producer.
/// </para>
/// <para>
/// A separate <see cref="ComponentIntegrationTemplate.BuilderWithServiceProvider"/> declaration
/// projects <c>Verb(Action&lt;IServiceProvider, FactoryType&gt; configure)</c>.
/// This required callback runs inside the target method's producer with the resolving service
/// provider. Builder construction, configuration, validation, and the build method run when that
/// producer is invoked, following the target method's lifetime. A null callback is rejected during
/// registration; an untyped <see langword="null"/> still selects the optional single-argument callback.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
internal sealed class ComponentIntegrationAttribute : Attribute
{
    /// <summary>The schema revision this declaration was compiled against.</summary>
    public const int SchemaVersion = 2;

    /// <summary>Initializes a new declaration.</summary>
    /// <param name="target">A composition seam from the shared catalog.</param>
    /// <param name="targetMethod">The registration method selected explicitly for this declaration.</param>
    /// <param name="factoryType">The public static factory type or publicly constructible builder type in the declaring assembly.</param>
    /// <param name="factoryMethodName">The name of either a public static factory method, whose supported overloads are copied, or a public zero-parameter instance method on a builder, which is projected with an optional <c>Action&lt;FactoryType&gt;</c> callback for eager construction and a required <c>Action&lt;IServiceProvider, FactoryType&gt;</c> callback for construction inside the target's producer.</param>
    /// <param name="template">The single signature and body template to project.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ComponentIntegrationAttribute(
        ComponentIntegrationTarget target,
        ComponentIntegrationMethod targetMethod,
        Type factoryType,
        string factoryMethodName,
        ComponentIntegrationTemplate template)
    {
        Target = target;
        TargetMethod = targetMethod;
        FactoryType = factoryType ?? throw new ArgumentNullException(nameof(factoryType));
        FactoryMethodName = factoryMethodName ?? throw new ArgumentNullException(nameof(factoryMethodName));
        Template = template;
    }

    /// <summary>Gets the fully qualified metadata name of the composition surface.</summary>
    public ComponentIntegrationTarget Target { get; }

    /// <summary>Gets the name of the member invoked on the surface.</summary>
    public ComponentIntegrationMethod TargetMethod { get; }

    /// <summary>Gets the single signature and implementation selected by this declaration.</summary>
    public ComponentIntegrationTemplate Template { get; }

    /// <summary>Gets or sets the parameter list, without parentheses, for a custom implementation.</summary>
    public string? Signature { get; set; }

    /// <summary>
    /// Gets or sets the complete C# block for a custom implementation. Supported placeholders are
    /// $receiver$, $arguments$, $targetMethod$, $seamType$, $factoryType$, $factoryMethod$, and $contract$.
    /// Use qualified type names; only the seam namespace is imported. The body includes its own return.
    /// </summary>
    public string? Body { get; set; }

    /// <summary>Gets the type in this assembly holding the factory method.</summary>
    public Type FactoryType { get; }

    /// <summary>Gets the name of the factory method.</summary>
    public string FactoryMethodName { get; }

    /// <summary>
    /// Gets or sets the name of the generated verb. Defaults to <see cref="FactoryMethodName"/>.
    /// </summary>
    public string? Verb { get; set; }

    /// <summary>
    /// Gets or sets an explicit generic type argument for <see cref="TargetMethod"/>.
    /// Optional; omit it to let overload resolution infer the argument.
    /// </summary>
    public Type? Contract { get; set; }
}
