using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit.Sdk;

namespace Assimalign.Cohesion.Resilience.Retry.Tests;

public class UnitTest1
{
    [Fact]
    public async Task SuccessResultRetryTest()
    {
        TestClient client = new TestClient();

        ResiliencePipeline<bool> pipeline = new ResiliencePipelineBuilder<bool>()
            .UseRetry(options =>
            {
                options.MaxRetryAttempts = 5;
                options.Delay = TimeSpan.FromSeconds(1);
                options.ShouldRetry = static async args =>
                {
                    if (!args.Outcome.IsSuccess)
                    {
                        return true;
                    }

                    return false;
                };
            })
            .Build();

        bool result = await pipeline.ExecuteAsync(async (_, state) =>
        {
            return await ((TestClient)state!).SendAsync();
        }, client);

        Assert.Equal(3, client.RetryCount);
        Assert.True(result);
    }

    [Fact]
    public async Task FailureRetryTest()
    {
        TestClient client = new TestClient();

        ResiliencePipeline<bool> pipeline = new ResiliencePipelineBuilder<bool>()
            .UseRetry(options =>
            {
                options.MaxRetryAttempts = 1;
                options.Delay = TimeSpan.FromSeconds(1);
                options.ShouldRetry = static async args =>
                {
                    if (!args.Outcome.IsSuccess)
                    {
                        return true;
                    }

                    return false;
                };
            })
            .Build();


        var exception = await Assert.ThrowsAsync<TestException>(async () =>
        {
            bool result = await pipeline.ExecuteAsync(static async (_, state) => await ((TestClient)state!).SendAsync(), client).AsTask();



        });

        Assert.Equal(2, client.RetryCount);
    }



    public class TestClient
    {
        private int _retryCount = 0;

        public int RetryCount => _retryCount;

        public async Task<bool> SendAsync()
        {
            await Task.Delay(1000);
            
            _retryCount++;

            if (_retryCount < 3)
            {

               
                throw new TestException();
            }

            return true;
        }
    }

    public class TestException : Exception;
}
