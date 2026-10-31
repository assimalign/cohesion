using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.ErrorHandling;

// Owner decision 34 (#1380): the OnError hook registers through the application's service registrations
// as builder.Services.AddErrorHandling(errors => errors.OnError(...)). Naming the public
// ErrorHandlingBuilder.Build instance method selects the generator's builder template, which takes the
// configure callback. The shared catalog names the seam without a reference, so this project takes no reference to DI.
// Decision 35: every IHttpFeature registration is a singleton, which Web.Hosting enforces at Build.
// "Add configures, Use places" (owner decision, 2026-10-28): the hook also carries the exception boundary's options
// (errors.Options), which UseErrorHandling() reads from the last registered hook when it places the boundary.
[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(ErrorHandlingBuilder),
    factoryMethodName: nameof(ErrorHandlingBuilder.Build),
    template:         ComponentIntegrationTemplate.Builder,
    Verb = "AddErrorHandling",
    Contract = typeof(IHttpFeature))]

[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(ErrorHandlingBuilder),
    factoryMethodName: nameof(ErrorHandlingBuilder.Build),
    template:         ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddErrorHandling",
    Contract = typeof(IHttpFeature))]

// "Add configures, Use places" (owner decision, 2026-10-28): builder.Services.AddStatusCodePages(pages => ...) builds
// the configured middleware and registers it, and UseStatusCodePages() places the registration it finds in
// IWebApplicationContext.Middleware. Naming the public StatusCodePagesBuilder.Build instance method selects the
// generator's builder templates.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(StatusCodePagesBuilder),
    factoryMethodName: nameof(StatusCodePagesBuilder.Build),
    template:          ComponentIntegrationTemplate.Builder,
    Verb = "AddStatusCodePages",
    Contract = typeof(IWebApplicationMiddleware))]

[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(StatusCodePagesBuilder),
    factoryMethodName: nameof(StatusCodePagesBuilder.Build),
    template:          ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddStatusCodePages",
    Contract = typeof(IWebApplicationMiddleware))]
