using System;
using Xunit;

namespace Assimalign.Cohesion.Resilience.Tests;

public class UnitTest1
{
    [Fact]
    public void Test1()
    {
        var pipeline = new ResiliencePipelineBuilder()
            .UseStrategy(async (callback, context, state) =>
            {
                try
                {
                    await callback(context, state);
                    return true;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            })
            .UseStrategy(async (callback, context, state) =>
            {
                try
                {
                    await callback(context, state);
                    return true;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            })
            .UseStrategy(async (callback, context, state) =>
            {
                try
                {
                    await callback(context, state);
                    return true;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            })
            .Build();

        int i = 0;

        pipeline.Execute((context, state) =>
        {
            i++;
            throw new MyException(i);
        }, null);

    }

    public class MyException(int Index) : Exception;
}
