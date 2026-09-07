using System;

using Assimalign.Cohesion.Web.Hosting;

using EnabledWeb;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
await using WebApplication application = builder.Build();

Console.WriteLine($"{Resource.Application}/{Resource.Name}:{Resource.Kind}");
Assimalign.Cohesion.Core.EndpointAddress httpEndpoint = Resource.Endpoints.Http;
Assimalign.Cohesion.Hosting.ResourceMount cacheMount = Resource.Mounts.Cache;
string cachePath = cacheMount.Path ?? string.Empty;
int pageSize = Resource.Settings.OrdersPageSize.Get<int>();
Assimalign.Cohesion.Core.EndpointAddress databaseEndpoint = Resource.References.InventoryDatabase.Db.Endpoint;
Uri databaseUrl = Resource.References.InventoryDatabase.Db.Url;
bool hasDatabaseUrl = Resource.References.InventoryDatabase.Db.TryGetUrl(out Uri? optionalDatabaseUrl);
Assimalign.Cohesion.Connections.IConnectionFactory connectionFactory =
    Resource.References.InventoryDatabase.Db.ConnectionFactory();
_ = (httpEndpoint, cacheMount, cachePath, pageSize, databaseEndpoint, databaseUrl, hasDatabaseUrl, optionalDatabaseUrl, connectionFactory);
_ = Resource.Manifest;
