using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Names a gateway application and describes how its model is obtained at application-set start.
/// </summary>
public sealed class ApplicationDeclaration
{
    /// <summary>Initializes a generated application declaration.</summary>
    /// <param name="name">The member application's declared identity.</param>
    /// <param name="resolver">The member's describe or export resolver.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="resolver"/> is <see langword="null"/>.
    /// </exception>
    public ApplicationDeclaration(ApplicationName name, IApplicationModelResolver resolver)
    {
        Name = name;
        Resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>Gets the declared member application identity.</summary>
    public ApplicationName Name { get; }

    /// <summary>Gets the control-plane model resolver.</summary>
    public IApplicationModelResolver Resolver { get; }
}
