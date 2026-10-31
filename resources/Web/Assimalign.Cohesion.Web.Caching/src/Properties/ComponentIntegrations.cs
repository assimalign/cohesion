using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Caching;

// "Add configures, Use places" (owner decision, 2026-10-28): builder.Services.AddOutputCache(cache => ...) builds the
// configured middleware and registers it, and UseOutputCache() places the registration it finds in
// IWebApplicationContext.Middleware. Naming the builder's public Build instance method selects the generator's
// builder templates, so this project takes no reference to DI.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(OutputCacheBuilder),
    factoryMethodName: nameof(OutputCacheBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddOutputCache",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(OutputCacheBuilder),
    factoryMethodName: nameof(OutputCacheBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddOutputCache",
    Contract = typeof(IWebApplicationMiddleware))]
