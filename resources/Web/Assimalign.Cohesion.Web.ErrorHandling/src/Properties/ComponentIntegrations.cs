using Assimalign.Cohesion;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.ErrorHandling;

// Owner decision 34 (#1380): the OnError hook registers through the application's service registrations
// as builder.Services.AddErrorHandling(errors => errors.OnError(...)). Naming the public
// ErrorHandlingBuilder.Build instance method selects the generator's builder template, which takes the
// configure callback. The shared catalog names the seam without a reference, so this project takes no reference to DI.
// Decision 35: every IHttpFeature registration is a singleton, which Web.Hosting enforces at Build.
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
