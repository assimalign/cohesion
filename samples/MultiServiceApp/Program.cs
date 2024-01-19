using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Net.Http;


var builder = HostBuilder.Create();

builder.AddLogSpace();
builder.AddEventHub();
builder.AddMessageHub();
builder.AddConfigurationStore();
builder.AddOGraph();
builder.AddHttpServer(server =>
{

});