using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.SecurityHeaders;

// "Add configures, Use places" (owner decision, 2026-10-28): builder.Services.AddSecurityHeaders(headers => ...)
// builds the configured middleware and registers it, and UseSecurityHeaders() places the registration it finds
// in IWebApplicationContext.Middleware. Naming the builder's public Build instance method selects the
// generator's builder templates, so this project takes no reference to DI.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(SecurityHeadersBuilder),
    factoryMethodName: nameof(SecurityHeadersBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddSecurityHeaders",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(SecurityHeadersBuilder),
    factoryMethodName: nameof(SecurityHeadersBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddSecurityHeaders",
    Contract = typeof(IWebApplicationMiddleware))]
