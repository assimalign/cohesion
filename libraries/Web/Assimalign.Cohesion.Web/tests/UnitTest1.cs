namespace Assimalign.Cohesion.Web.Hosting.Tests;

using Assimalign.Cohesion.Hosting;

public class UnitTest1
{
    [Fact]
    public void Test1()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions()
        {

        });

        var app = builder.Build();

        app.Use((context, next) =>
        {
            return next.Invoke(context);
        });

        app.Run();
    }
}
