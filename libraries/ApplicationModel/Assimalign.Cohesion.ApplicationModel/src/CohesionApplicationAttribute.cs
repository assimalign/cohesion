using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Records the build-selected Cohesion application name in gateway assembly metadata.
/// </summary>
/// <remarks>
/// The SDK emits this attribute for tooling. The application runtime receives the same
/// name directly through <see cref="Application.CreateBuilder(ApplicationName, string[])" />
/// and does not discover it through reflection.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class CohesionApplicationAttribute : Attribute
{
    /// <summary>Initializes the assembly metadata with its Cohesion application name.</summary>
    /// <param name="name">The RFC 1123 application name compiled into the gateway.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty or white space.</exception>
    public CohesionApplicationAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Gets the application name recorded for tooling.</summary>
    public string Name { get; }
}
