using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Testing;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Cors.Tests;

public sealed class CorsSettingTests
{
    private const string Shop = "https://shop.example";
    private const string Partner = "https://partner.example";

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Setting: AddCors registers the cors setting the application lists")]
    public async Task AddCors_WithDefaultPolicy_ShouldRegisterTheCorsSetting()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act
        factory.Builder.Services.AddCors(cors => cors.Options.AddDefaultPolicy(policy => policy.WithOrigins(Shop)));

        // Assert
        IWebApplicationSetting<CorsPolicy?> setting = factory.Application.Context.Settings
            .OfType<IWebApplicationSetting<CorsPolicy?>>()
            .ShouldHaveSingleItem();
        setting.Name.ShouldBe("cors");
        setting.Value.ShouldNotBeNull().Origins.ShouldBe([Shop]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Setting: A default policy set while the application runs applies from the next request")]
    public async Task UseCors_WhenTheSettingChanges_ShouldApplyTheNewDefaultPolicy()
    {
        // Arrange
        CorsBuilder cors = new();
        cors.Options.AddDefaultPolicy(policy => policy.WithOrigins(Shop));
        IWebApplicationSetting<CorsPolicy?> setting = cors.Build();
        TestPipelineBuilder builder = new(setting);
        builder.UseCors();
        IWebApplicationPipeline pipeline = builder.Build();

        // Act
        string? before = await AllowedOriginAsync(pipeline, Partner);
        setting.Set(new CorsPolicyBuilder().WithOrigins(Partner).Build());
        string? after = await AllowedOriginAsync(pipeline, Partner);
        string? previous = await AllowedOriginAsync(pipeline, Shop);

        // Assert
        before.ShouldBeNull();
        after.ShouldBe(Partner);
        previous.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Cors] - Setting: Changing the options after AddCors built the setting has no effect")]
    public void Build_ThenOptionsChange_ShouldKeepTheSnapshot()
    {
        // Arrange
        CorsBuilder cors = new();
        IWebApplicationSetting<CorsPolicy?> setting = cors.Build();

        // Act
        cors.Options.AddDefaultPolicy(policy => policy.WithOrigins(Shop));

        // Assert
        setting.Value.ShouldBeNull();
    }

    private static async Task<string?> AllowedOriginAsync(IWebApplicationPipeline pipeline, string origin)
    {
        CorsTestContext context = new(HttpMethod.Get, origin: origin);
        await pipeline.ExecuteAsync(context, CancellationToken.None);
        return context.ResponseHeader(HttpHeaderKey.AccessControlAllowOrigin);
    }
}
