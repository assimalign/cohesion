using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.ForwardedHeaders;

// "Add configures, Use places" (owner decision, 2026-10-28): builder.Services.AddForwardedHeaders(forwarded => ...)
// builds the middleware over the configured trust model and registers it, and UseForwardedHeaders() places the
// registration it finds in IWebApplicationContext.Middleware. Naming the public ForwardedHeadersBuilder.Build
// instance method selects the generator's builder templates, so this project takes no reference to DI.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(ForwardedHeadersBuilder),
    factoryMethodName: nameof(ForwardedHeadersBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddForwardedHeaders",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(ForwardedHeadersBuilder),
    factoryMethodName: nameof(ForwardedHeadersBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddForwardedHeaders",
    Contract = typeof(IWebApplicationMiddleware))]
