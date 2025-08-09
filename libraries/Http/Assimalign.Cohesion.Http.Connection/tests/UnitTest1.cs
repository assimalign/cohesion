namespace Assimalign.Cohesion.Http.Connection.Tests;

public class UnitTest1
{
    [Fact]
    public void Test1()
    {
        Configure(options =>
        {
            options.UseHttp1(options =>
            {
                options.Use(async (connection, context, next) =>
                {


                    await next.Invoke(connection, context);
                });
            });
        });

    }
    
    public void Configure(Action<HttpListenerOptions> configure)
    {

    }
}
