using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Testing;

using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Api.Tests;

/// <summary>
/// End-to-end coverage for uploaded-file binding (#1061): typed handlers bind <see cref="IHttpFormFile"/>
/// parameters, sequences of the files sent under one field name, and the collection of every file from a
/// <c>multipart/form-data</c> body; the Http.Forms limits apply, and a body over one is answered
/// <c>413 Content Too Large</c>.
/// </summary>
public class EndpointFileBindingTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    private static WebApplicationTestFactory CreateFactory(HttpFormOptions? formOptions = null)
    {
        WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();

        if (formOptions is not null)
        {
            // The limits an application sets: the form is parsed through the exchange's form feature, so
            // installing one with its own options ahead of the endpoint applies them.
            factory.Application.Use(async (context, next) =>
            {
                context.Features.Set<IHttpFormFeature>(new HttpFormFeature(context.Request, formOptions));
                await next.Invoke(context);
            });
        }

        factory.Application.UseRouting();
        return factory;
    }

    private static ByteArrayContent File(string text, string? contentType = null)
    {
        ByteArrayContent content = new(Encoding.UTF8.GetBytes(text));
        if (contentType is not null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        return content;
    }

    private static async Task<string> ReadAsync(IHttpFormFile file)
    {
        using Stream stream = file.OpenReadStream();
        using StreamReader reader = new(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Files: an IHttpFormFile binds the uploaded file's name, type and content")]
    public async Task MapPost_SingleFile_ShouldBindTheUpload()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.MapPost("/avatars", async (IHttpFormFile avatar, [FromForm] string caption) =>
            $"{avatar.Name}|{avatar.FileName}|{avatar.ContentType}|{avatar.Length}|{await ReadAsync(avatar)}|{caption}");

        using HttpClient client = factory.CreateClient();
        using MultipartFormDataContent form = new()
        {
            { File("hello", "text/plain"), "avatar", "me.txt" },
            { new StringContent("profile"), "caption" }
        };

        // Act
        using HttpResponseMessage response = await client.PostAsync("/avatars", form, cancellation.Token);

        // Assert — the file and the field come from the same parse.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("avatar|me.txt|text/plain|5|hello|profile");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Files: several files under one field name bind as a list, and the collection holds every file")]
    public async Task MapPost_MultipleFiles_ShouldBindEveryFile()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.MapPost("/albums", (IReadOnlyList<IHttpFormFile> photos, IHttpFormFile[] scans, IHttpFormFileCollection all) =>
            $"{string.Join(",", photos.Select(photo => photo.FileName))}|{scans.Length}|{all.Count}");

        using HttpClient client = factory.CreateClient();
        using MultipartFormDataContent form = new()
        {
            { File("1"), "photos", "one.png" },
            { File("22"), "photos", "two.png" },
            { File("333"), "notes", "readme.txt" }
        };

        // Act
        using HttpResponseMessage response = await client.PostAsync("/albums", form, cancellation.Token);

        // Assert — both photos in the order they were sent, no scans, three files in all.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("one.png,two.png|0|3");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Files: a missing required file is answered 400, and a missing optional one binds null")]
    public async Task MapPost_MissingFile_ShouldAnswer400OrBindNull()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        bool requiredRan = false;
        factory.Application.MapPost("/required", (IHttpFormFile upload) =>
        {
            requiredRan = true;
            return upload.FileName;
        });
        factory.Application.MapPost("/optional", (IHttpFormFile? upload) => upload is null ? "none" : upload.FileName);

        using HttpClient client = factory.CreateClient();
        using MultipartFormDataContent required = new() { { new StringContent("x"), "field" } };
        using MultipartFormDataContent optional = new() { { new StringContent("x"), "field" } };

        // Act
        using HttpResponseMessage missing = await client.PostAsync("/required", required, cancellation.Token);
        using HttpResponseMessage absent = await client.PostAsync("/optional", optional, cancellation.Token);

        // Assert
        missing.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        using JsonDocument problem = JsonDocument.Parse(await missing.Content.ReadAsStringAsync(cancellation.Token));
        problem.RootElement.GetProperty("errors").GetProperty("upload")[0].GetString().ShouldBe("The file is required.");
        requiredRan.ShouldBeFalse();
        (await absent.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("none");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Files: an upload over the Http.Forms size limit is answered 413")]
    public async Task MapPost_FileOverSizeLimit_ShouldAnswer413()
    {
        // Arrange — the application limits each multipart section to 16 bytes.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(new HttpFormOptions { MultipartBodyLengthLimit = 16 });

        bool ran = false;
        factory.Application.MapPost("/uploads", (IHttpFormFile upload) =>
        {
            ran = true;
            return upload.FileName;
        });

        using HttpClient client = factory.CreateClient();
        using MultipartFormDataContent small = new() { { File("0123456789"), "upload", "small.bin" } };
        using MultipartFormDataContent large = new() { { File(new string('x', 64)), "upload", "large.bin" } };

        // Act
        using HttpResponseMessage accepted = await client.PostAsync("/uploads", small, cancellation.Token);
        using HttpResponseMessage rejected = await client.PostAsync("/uploads", large, cancellation.Token);

        // Assert — RFC 9110 §15.5.14: content larger than the server is willing to process.
        accepted.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        rejected.StatusCode.ShouldBe(NetHttpStatusCode.RequestEntityTooLarge);
        rejected.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using JsonDocument problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync(cancellation.Token));
        problem.RootElement.GetProperty("status").GetInt32().ShouldBe(413);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Files: a malformed multipart body is answered 400")]
    public async Task MapPost_MalformedMultipart_ShouldAnswer400()
    {
        // Arrange — a section header line with no colon.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.MapPost("/uploads", (IHttpFormFile upload) => upload.FileName);

        using HttpClient client = factory.CreateClient();
        using StringContent malformed = new("--B\r\nnot a header\r\n\r\nx\r\n--B--\r\n");
        malformed.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=B");

        // Act
        using HttpResponseMessage response = await client.PostAsync("/uploads", malformed, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation.Token));
        problem.RootElement.GetProperty("errors").TryGetProperty("$form", out _).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Files: file parameters are described with the FormFile source")]
    public async Task MapPost_FileEndpoint_ShouldDescribeFormFileParameters()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application
            .MapPost("/documents", (IHttpFormFile document, IReadOnlyList<IHttpFormFile> attachments) => "ok")
            .WithName("documents");

        using HttpClient client = factory.CreateClient();
        using MultipartFormDataContent form = new() { { File("d"), "document", "d.txt" } };
        using HttpResponseMessage response = await client.PostAsync("/documents", form, cancellation.Token);

        // Act
        IRouter router = factory.Application.Context.Features.OfType<IRouterFeature>().Single().Router;
        IRouterRoute route = router.Routes.Single(candidate => candidate.Metadata.GetMetadata<RouteNameMetadata>()?.RouteName == "documents");
        IReadOnlyList<EndpointParameterMetadata> parameters = route.Metadata.GetOrderedMetadata<EndpointParameterMetadata>();

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        parameters.Count.ShouldBe(2);
        parameters[0].Name.ShouldBe("document");
        parameters[0].Source.ShouldBe(EndpointParameterSource.FormFile);
        parameters[0].Type.ShouldBe(typeof(IHttpFormFile));
        parameters[0].IsRequired.ShouldBeTrue();
        parameters[1].Name.ShouldBe("attachments");
        parameters[1].Source.ShouldBe(EndpointParameterSource.FormFile);
        parameters[1].Type.ShouldBe(typeof(IReadOnlyList<IHttpFormFile>));
        parameters[1].IsRequired.ShouldBeFalse();
    }
}
