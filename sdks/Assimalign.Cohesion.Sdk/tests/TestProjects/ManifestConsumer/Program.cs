using System;

using ManifestConsumer;

Console.WriteLine($"{Resource.Application}/{Resource.Name}:{Resource.Kind}");
Console.WriteLine(Resource.References.InventoryWeb.Http.Endpoint);
