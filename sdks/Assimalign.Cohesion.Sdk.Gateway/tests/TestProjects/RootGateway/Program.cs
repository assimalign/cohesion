using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
_ = Applications.Appa;
_ = References.Appa.WebHttp;
builder.UseGateway(args);
