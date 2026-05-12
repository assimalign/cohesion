namespace Assimalign.Cohesion.Web.App.Tests;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Web;
using ApplicationModel;
using System;

public class UnitTest1
{
    [Fact]
    public void Test1()
    {
        var builder = new HostBuilder(new HostOptions()
        {

        });

        builder.ConfigureApplication(builder => 
        {

            
            var app = builder.Build();

            return app;
        });


        var builders = new WebApplicationBuilder();

        IServiceProviderFactory factory = new ServiceProviderFactory()
            .Register("", new ServiceProviderOptions()
            {
                
            }, 
            builder => 
            { 
                builder.
            })
            .Build();

        builder.Services.AddSingleton(new object());
        builder.Services.AddSingleton(new object());

        var app = builder.Build();

        app.Use([Authorize("")] async (context, next) =>
        {

            await next.Invoke(context);
        });
    }

    public class MyWebAppBuilder


    public class AuthorizeAttribute : Attribute
    {
        public AuthorizeAttribute(string policy)
        {
            
        }
    }
}
