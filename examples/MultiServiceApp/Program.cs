using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Web;

var builder = HostBuilder.Create();


builder.AddWebServer(server =>
{

})
.AddDnsServer(myserver =>
{
    myserver.AddARecord()
});




var host = builder.Build();


host.Run();