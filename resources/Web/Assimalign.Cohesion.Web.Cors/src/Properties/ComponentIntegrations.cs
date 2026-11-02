using Assimalign.Cohesion;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Cors;

// CORS registers through the application's service registrations as builder.Services.AddCors(...), the
// way every Web feature registers. The verb configures everything; UseCors() only places the middleware
// (owner decision, 2026-10-28). What it registers is the application's cors setting, a singleton
// IWebApplicationSetting the context lists on Settings, so an operator surface can change the default
// policy while the application runs. The shared catalog names the seam without a reference, so this
// project takes no reference to DI.
[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(CorsBuilder),
    factoryMethodName: nameof(CorsBuilder.Build),
    template:         ComponentIntegrationTemplate.Builder,
    Verb = "AddCors",
    Contract = typeof(IWebApplicationSetting))]

[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(CorsBuilder),
    factoryMethodName: nameof(CorsBuilder.Build),
    template:         ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddCors",
    Contract = typeof(IWebApplicationSetting))]
