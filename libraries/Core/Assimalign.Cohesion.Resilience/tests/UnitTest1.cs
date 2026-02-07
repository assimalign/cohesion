using System;
using System.Collections.Generic;
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
                    await callback.Invoke(context, state);
                    return Outcome.Success();
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
            switch (i)
            {
                case 1: throw new MyException1();
                case 2: throw new MyException2();
                case 3: throw new MyException3();
            }
            throw new MyException1();
        
        }, null);
    }

    public class MyException1() : Exception;
    public class MyException2() : Exception;
    public class MyException3() : Exception;
}
