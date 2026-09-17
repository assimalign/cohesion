using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
_ = Applications.AppA;
_ = References.AppA.WebHttp;
builder.UseGateway(args);
