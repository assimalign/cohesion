using System;

using Assimalign.Cohesion.Database.Hosting;

using EnabledDatabase;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);
await using DatabaseApplication application = builder.Build();

Console.WriteLine($"{Resource.Application}/{Resource.Name}:{Resource.Kind}");
Uri adminEndpoint = Resource.Endpoints.Admin;
Uri databaseEndpoint = Resource.Endpoints.Db;
Assimalign.Cohesion.Hosting.Resources.ResourceMount dataMount = Resource.Mounts.Data;
string dataPath = dataMount.Path ?? string.Empty;
int poolSize = Resource.Settings.DatabasePoolSize.Get<int>();
Assimalign.Cohesion.Connections.IConnectionFactory dependencyFactory =
    Resource.References.InventoryStorage.Db.ConnectionFactory();
_ = (adminEndpoint, databaseEndpoint, dataMount, dataPath, poolSize, dependencyFactory);
_ = Resource.Manifest;
