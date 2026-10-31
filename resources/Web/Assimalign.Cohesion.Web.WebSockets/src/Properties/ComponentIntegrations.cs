using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.WebSockets;

// "Add configures, Use places" (owner decision, 2026-10-28): builder.Services.AddWebSockets(webSockets => ...) builds
// the configured policy middleware and registers it, and UseWebSockets() places the registration it finds in
// IWebApplicationContext.Middleware. Naming the builder's public Build instance method selects the generator's builder
// templates, so this project takes no reference to DI.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(WebSocketsBuilder),
    factoryMethodName: nameof(WebSocketsBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddWebSockets",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(WebSocketsBuilder),
    factoryMethodName: nameof(WebSocketsBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddWebSockets",
    Contract = typeof(IWebApplicationMiddleware))]
