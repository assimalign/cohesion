using System;

namespace Assimalign.Cohesion.ApplicationModel;

// Deviates from the repo interface-first rule per the owner-approved BYO design (2026-09-25): the
// design names this sealed fluent handle as the return type of UseSecretStore; it only writes
// provider bindings into one application's ApplicationProviders and has no behaviour worth substituting.

/// <summary>
/// Assigns the optional gateway roles of one SecretStore resource registered with
/// <c>UseSecretStore(store)</c>: the application's certificate authority and its trusted-issuer
/// store.
/// </summary>
/// <remarks>
/// <para>
/// Each role is a single slot in the <see cref="ApplicationProviders"/> of the surface the store was
/// registered on — an application builder, or one application-set member. Assigning a role again for
/// the same store changes nothing; assigning it while another provider or another store holds the
/// slot throws <see cref="InvalidOperationException"/> rather than silently replacing that
/// registration.
/// </para>
/// <code>
/// builder.UseSecretStore(secrets)
///     .AsCertificateAuthority()
///     .AsTrustStore();
///
/// set.AddApplication(Applications.Platform, platform =&gt; platform
///     .UseSecretStore("platform-secrets")
///     .AsCertificateAuthority()
///     .AsTrustStore());
/// </code>
/// </remarks>
public sealed class SecretStoreProviderBuilder
{
    private readonly Func<ApplicationProviders> _providers;

    /// <summary>
    /// Initializes a handle over one registered SecretStore.
    /// </summary>
    /// <param name="providers">
    /// Reads the current registrations of the surface the store was registered on. The handle reads
    /// them on every call instead of keeping one instance, because an application-set member's
    /// registrations are replaced by a frozen snapshot when its callback returns.
    /// </param>
    /// <param name="store">The SecretStore resource's name.</param>
    internal SecretStoreProviderBuilder(Func<ApplicationProviders> providers, ResourceName store)
    {
        _providers = providers;
        Store = store;
    }

    /// <summary>
    /// Gets the current provider registrations of the surface the SecretStore was registered on: the
    /// <see cref="IApplicationBuilder.Providers"/> of an application built in code, or the
    /// <see cref="IApplicationProviderBuilder.Providers"/> of an application-set member. For a set
    /// member this is the frozen snapshot once its callback has returned, so a later role assignment
    /// throws instead of writing registrations the member's model never sees.
    /// </summary>
    public ApplicationProviders Providers => _providers();

    /// <summary>
    /// Gets the name of the SecretStore resource this handle binds.
    /// </summary>
    public ResourceName Store { get; }

    /// <summary>
    /// Makes this SecretStore the application's certificate authority: endpoints that declare a
    /// certificate mount without a source get their TLS leaf from it.
    /// </summary>
    /// <returns>This handle, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="ApplicationProviders.CertificateAuthority"/> already binds another provider or
    /// another resource.
    /// </exception>
    /// <remarks>
    /// Sets <see cref="ApplicationProviders.CertificateAuthority"/> to a binding of
    /// <see cref="Store"/> and a <see cref="SecretStoreCertificateAuthority"/>. The gateway still
    /// issues the SecretStore's own endpoint leaf from its development authority, because the store
    /// cannot issue its first certificate itself.
    /// </remarks>
    public SecretStoreProviderBuilder AsCertificateAuthority()
    {
        ApplicationProviders providers = Providers;
        ResourceProviderBinding<IResourceCertificateAuthority>? current = providers.CertificateAuthority;
        if (current is null)
        {
            providers.CertificateAuthority = new ResourceProviderBinding<IResourceCertificateAuthority>(
                Store,
                new SecretStoreCertificateAuthority());
            return this;
        }

        if (current.Resource == Store && current.Provider is SecretStoreCertificateAuthority)
        {
            return this;
        }

        throw new InvalidOperationException(
            $"The application's certificate authority is already registered ({Describe(current.Resource, current.Provider)}). " +
            $"An application has one certificate authority; remove that registration before making SecretStore " +
            $"'{Store}' the authority.");
    }

    /// <summary>
    /// Makes this SecretStore the application's trusted-issuer store: peer trust grants are
    /// persisted in it and read back from it.
    /// </summary>
    /// <returns>This handle, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="ApplicationProviders.TrustStore"/> already binds another provider or another
    /// resource.
    /// </exception>
    /// <remarks>
    /// Sets <see cref="ApplicationProviders.TrustStore"/> to a binding of <see cref="Store"/> and a
    /// <see cref="SecretStoreTrustedIssuerStore"/>.
    /// </remarks>
    public SecretStoreProviderBuilder AsTrustStore()
    {
        ApplicationProviders providers = Providers;
        ResourceProviderBinding<ITrustedIssuerStore>? current = providers.TrustStore;
        if (current is null)
        {
            providers.TrustStore = new ResourceProviderBinding<ITrustedIssuerStore>(
                Store,
                new SecretStoreTrustedIssuerStore());
            return this;
        }

        if (current.Resource == Store && current.Provider is SecretStoreTrustedIssuerStore)
        {
            return this;
        }

        throw new InvalidOperationException(
            $"The application's trusted-issuer store is already registered ({Describe(current.Resource, current.Provider)}). " +
            $"An application has one trusted-issuer store; remove that registration before making SecretStore " +
            $"'{Store}' the trust store.");
    }

    private static string Describe(ResourceName? resource, object provider) =>
        resource is ResourceName name
            ? $"'{provider.GetType().Name}' bound to resource '{name}'"
            : $"'{provider.GetType().Name}' bound to no resource";
}
