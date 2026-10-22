using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Validation;

// Owner decision 34 (#1380): request validation registers through the application's service
// registrations as builder.Services.AddValidation(validation => ...). Naming the public
// ValidationBuilder.Build instance method selects the generator's builder template, whose configure
// callback is optional (decision 39, #1395). The shared catalog names the seam without a reference, so this project takes no
// reference to DI. Decision 35: every IHttpFeature registration is a singleton, which Web.Hosting
// enforces at Build.
[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(ValidationBuilder),
    factoryMethodName: nameof(ValidationBuilder.Build),
    template:         ComponentIntegrationTemplate.Builder,
    Verb = "AddValidation",
    Contract = typeof(IHttpFeature))]

[assembly: ComponentIntegration(
    target:           ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:     ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(ValidationBuilder),
    factoryMethodName: nameof(ValidationBuilder.Build),
    template:         ComponentIntegrationTemplate.BuilderWithServiceProvider,
    Verb = "AddValidation",
    Contract = typeof(IHttpFeature))]
