using System;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// A builder pattern for creating a <see cref="IHost"/>.
/// </summary>
public interface IHostBuilder
{
    /// <summary>
    /// Builds the <see cref="IHost"/>.
    /// </summary>
    /// <returns>The configured host.</returns>
    IHost Build();
}
