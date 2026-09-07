using System;

using EnabledDatabase;

Console.WriteLine($"{Resource.Application}/{Resource.Name}:{Resource.Kind}");
Assimalign.Cohesion.Core.EndpointAddress adminEndpoint = Resource.Endpoints.Admin;
Assimalign.Cohesion.Core.EndpointAddress databaseEndpoint = Resource.Endpoints.Db;
string dataPath = Resource.Mounts.Data;
int poolSize = Resource.Settings.DatabasePoolSize.Get<int>();
_ = (adminEndpoint, databaseEndpoint, dataPath, poolSize);
_ = Resource.Manifest;
