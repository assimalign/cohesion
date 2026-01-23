using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Timeout.Tests;

public class UnitTest1
{
    [Fact]
    public async Task Test1()
    {
        //TestClient client = new TestClient();

        //ResiliencePipeline<bool> pipeline = new ResiliencePipelineBuilder<bool>()
        //    .UseTimeout(options =>
        //    {

        //    })
        //    .UseRetry(options =>
        //    {
        //        options.MaxRetryAttempts = 5;
        //        options.Delay = TimeSpan.FromSeconds(1);
        //        options.ShouldRetry = static async args =>
        //        {
        //            if (!args.Outcome.IsSuccess)
        //            {
        //                return true;
        //            }

        //            return false;
        //        };
        //    })
        //    .Build();

        //int i = 0;

        //bool result = await pipeline.ExecuteAsync(async (_, state) =>
        //{
        //    i++;
        //    return await state.SendAsync();
        //}, client);

        //Assert.Equal(3, client.RetryCount);
        //Assert.True(result);
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
