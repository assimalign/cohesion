using System;

using EnabledWeb;

Console.WriteLine($"{Resource.Application}/{Resource.Name}:{Resource.Kind}");
Assimalign.Cohesion.Core.EndpointAddress httpEndpoint = Resource.Endpoints.Http;
string cachePath = Resource.Mounts.Cache;
int pageSize = Resource.Settings.OrdersPageSize.Get<int>();
Assimalign.Cohesion.Core.EndpointAddress databaseEndpoint = Resource.References.InventoryDatabase.Db.Endpoint;
Uri databaseUrl = Resource.References.InventoryDatabase.Db.Url;
bool hasDatabaseUrl = Resource.References.InventoryDatabase.Db.TryGetUrl(out Uri? optionalDatabaseUrl);
Assimalign.Cohesion.Connections.IConnectionFactory connectionFactory =
    Resource.References.InventoryDatabase.Db.ConnectionFactory();
_ = (httpEndpoint, cachePath, pageSize, databaseEndpoint, databaseUrl, hasDatabaseUrl, optionalDatabaseUrl, connectionFactory);
_ = Resource.Manifest;
