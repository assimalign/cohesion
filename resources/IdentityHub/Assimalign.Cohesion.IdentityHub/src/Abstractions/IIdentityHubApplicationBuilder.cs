using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// Defines the contract-only composition seam for an identity hub application.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is where an identity hub is defined in code.</b> Each <c>Add</c> verb declares one
/// identity object, already checked as a whole, and the application serves it locked: it is
/// read like an object created while the hub runs, and it changes only here. The verbs that
/// take a name or an identifier and a callback are extension members over these
/// (<see cref="IdentityHubApplicationBuilderExtensions"/>), so every way of declaring an object
/// ends in the same member.
/// </para>
/// <para>
/// A verb exists for a kind of object once the hub serves that kind. A kind the hub does not
/// serve yet has no verb, so nothing can be declared that the hub would then ignore.
/// </para>
/// </remarks>
public interface IIdentityHubApplicationBuilder
{
    /// <summary>
    /// Declares an audience for access tokens issued by this identity hub.
    /// </summary>
    /// <param name="audience">The exact audience identifier.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="audience"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The audience is already declared.</exception>
    IIdentityHubApplicationBuilder AddAudience(string audience);

    /// <summary>
    /// Registers an OAuth client in the identity hub's code-first configuration.
    /// </summary>
    /// <param name="clientId">The exact client identifier.</param>
    /// <param name="configure">The callback that configures grants, credentials, and audiences.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="clientId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The client is already registered.</exception>
    IIdentityHubApplicationBuilder AddClient(
        string clientId,
        Action<IdentityHubClientOptions> configure);

    /// <summary>
    /// Sets the store that holds the identity objects created while the hub runs.
    /// </summary>
    /// <remarks>
    /// The store stays owned by the caller. An application with no store keeps those objects in
    /// memory, in an <see cref="InMemoryIdentityHubStore"/>: they are gone when it stops, and
    /// two nodes do not share them. The objects defined in code never reach the store.
    /// </remarks>
    /// <param name="store">The store.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The application already has a store.</exception>
    IIdentityHubApplicationBuilder AddStore(IIdentityHubStore store);

    /// <summary>
    /// Sets the store that holds the identity objects created while the hub runs, created when
    /// the application is built.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once, with the application's context, and the application owns the
    /// store it returns: a store that is disposable is disposed with the application.
    /// <see cref="IIdentityHubApplicationContext.Directory"/> is not available to the factory,
    /// because the directory is created over the store.
    /// </remarks>
    /// <param name="factory">The factory that creates the store.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The application already has a store.</exception>
    IIdentityHubApplicationBuilder AddStore(Func<IIdentityHubApplicationContext, IIdentityHubStore> factory);

    /// <summary>
    /// Defines a user in code. The application serves it locked.
    /// </summary>
    /// <remarks>
    /// A user stored under the same identifier, which a user created under the same name has,
    /// is not served from then on: the one defined here is.
    /// </remarks>
    /// <param name="user">The user.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="user"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A user with the same identifier or the same name is already defined.
    /// </exception>
    IIdentityHubApplicationBuilder AddUser(IdentityUser user);

    /// <summary>
    /// Defines a group in code. The application serves it locked.
    /// </summary>
    /// <remarks>
    /// A group stored under the same identifier, which a group created under the same name has,
    /// is not served from then on: the one defined here is.
    /// </remarks>
    /// <param name="group">The group.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A group with the same identifier or the same name is already defined.
    /// </exception>
    IIdentityHubApplicationBuilder AddGroup(IdentityGroup group);

    /// <summary>
    /// Builds the identity hub application.
    /// </summary>
    /// <returns>The configured identity hub application.</returns>
    IIdentityHubApplication Build();
}
