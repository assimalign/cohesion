using System;

using Assimalign.Cohesion.Database.Hosting;

using EnabledDatabase;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);
await using DatabaseApplication application = builder.Build();

Console.WriteLine($"{Resource.Application}/{Resource.Name}:{Resource.Kind}");
Assimalign.Cohesion.Core.EndpointAddress adminEndpoint = Resource.Endpoints.Admin;
Assimalign.Cohesion.Core.EndpointAddress databaseEndpoint = Resource.Endpoints.Db;
Assimalign.Cohesion.Hosting.ResourceMount dataMount = Resource.Mounts.Data;
string dataPath = dataMount.Path ?? string.Empty;
int poolSize = Resource.Settings.DatabasePoolSize.Get<int>();
Assimalign.Cohesion.Connections.IConnectionFactory dependencyFactory =
    Resource.References.InventoryStorage.Db.ConnectionFactory();
_ = (adminEndpoint, databaseEndpoint, dataMount, dataPath, poolSize, dependencyFactory);
_ = Resource.Manifest;
