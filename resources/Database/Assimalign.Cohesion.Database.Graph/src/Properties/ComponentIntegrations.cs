using Assimalign.Cohesion;
using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Graph;

// builder.Services.AddGraph(name, configure): the engine registers through the application's service
// registrations, as Web features do (owner decision 34, #1380), without this project taking a DI
// reference. The engine name is a required argument, so the verb is the static-factory template over
// GraphComponents (decision 39, #1395). The product is a DatabaseEngineRegistration instance, not the engine:
// Database.Hosting reads the name to refuse a duplicate before any factory runs (decision 52) and
// builds the engine once, during application Build.
[assembly: ComponentIntegration(
    target:            ComponentIntegrationTarget.ServiceProviderBuilder,
    targetMethod:      ComponentIntegrationMethod.AddSingleton,
    factoryType:       typeof(GraphComponents),
    factoryMethodName: nameof(GraphComponents.CreateEngineRegistration),
    template:          ComponentIntegrationTemplate.Factory,
    Verb = "AddGraph",
    Contract = typeof(DatabaseEngineRegistration))]
