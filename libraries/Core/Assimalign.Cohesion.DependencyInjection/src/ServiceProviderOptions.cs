﻿using System;

namespace Assimalign.Cohesion.DependencyInjection;

/// <summary>
/// Options for configuring various behaviors of the default <see cref="IServiceProvider"/> implementation.
/// </summary>
public class ServiceProviderOptions
{
    // Avoid allocating objects in the default case
    internal static readonly ServiceProviderOptions Default = new();
    /// <summary>
    /// <c>true</c> to perform check verifying that scoped services never gets resolved from root provider; otherwise <c>false</c>. Defaults to <c>false</c>.
    /// </summary>
    public bool ValidateScopes { get; set; }
    /// <summary>
    /// <c>true</c> to perform check verifying that all services can be created during <c>BuildServiceProvider</c> call; otherwise <c>false</c>. Defaults to <c>false</c>.
    /// NOTE: this check doesn't verify open generics services.
    /// </summary>
    public bool ValidateOnBuild { get; set; }
}
