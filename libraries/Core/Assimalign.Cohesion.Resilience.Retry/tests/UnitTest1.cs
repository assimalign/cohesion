using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Retry.Tests;

public class UnitTest1
{
    [Fact]
    public async Task Test1()
    {
        using HttpClient client = new HttpClient();

        ResiliencePipeline<TestResult> pipeline = new ResiliencePipelineBuilder<TestResult>()
            .UseRetry(options =>
            {

            })
            .Build();



        TestResult result = await pipeline.ExecuteAsync(static async (context, state) =>
        {
            HttpResponseMessage httpResponseMessage = await state.SendAsync(new HttpRequestMessage()
            {

            });


            httpResponseMessage.EnsureSuccessStatusCode();

            using Stream stream = httpResponseMessage.Content.ReadAsStream();

            return JsonSerializer.Deserialize<TestResult>(stream);

        }, client);

    }


    public class TestResult
    {

    }
}
