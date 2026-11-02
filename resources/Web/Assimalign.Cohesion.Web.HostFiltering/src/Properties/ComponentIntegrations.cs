using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.HostFiltering;

// "Add configures, Use places" (owner decision, 2026-10-28): builder.Services.AddHostFiltering(hosts => ...) compiles
// the configured allowlist into the middleware and registers it, and UseHostFiltering() places the registration it
// finds in IWebApplicationContext.Middleware. Naming the public HostFilteringBuilder.Build instance method selects the
// generator's builder templates, so this project takes no reference to DI.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(HostFilteringBuilder),
    factoryMethodName: nameof(HostFilteringBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddHostFiltering",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(HostFilteringBuilder),
    factoryMethodName: nameof(HostFilteringBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddHostFiltering",
    Contract = typeof(IWebApplicationMiddleware))]
