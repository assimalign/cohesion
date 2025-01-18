using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Web;

var builder = HostBuilder.Create();


builder.AddWebServer(server =>
{

});




var host = builder.Build();


host.Run();