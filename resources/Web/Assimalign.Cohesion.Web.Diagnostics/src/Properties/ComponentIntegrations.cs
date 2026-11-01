using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Diagnostics;

// "Add configures, Use places" (owner decision, 2026-10-28): builder.Services.AddHttpLogging(logging => ...) builds the
// HTTP logging middleware over the logger and options it configures and registers it, and UseHttpLogging() places the
// registration it finds in IWebApplicationContext.Middleware. Naming the builder's public Build instance method selects
// the generator's builder templates, so this project takes no reference to DI. The provider template is how a Web.Hosting
// application hands the middleware its own ILoggerFactory.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(HttpLoggingBuilder),
    factoryMethodName: nameof(HttpLoggingBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddHttpLogging",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(HttpLoggingBuilder),
    factoryMethodName: nameof(HttpLoggingBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddHttpLogging",
    Contract = typeof(IWebApplicationMiddleware))]
