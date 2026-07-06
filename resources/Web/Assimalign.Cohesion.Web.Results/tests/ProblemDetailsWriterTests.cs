using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using Assimalign.Cohesion.Http;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Results.Tests;

/// <summary>
/// Verifies the AOT-safe <c>application/problem+json</c> writer (RFC 9457): standard-member rendering,
/// the <c>"about:blank"</c> default, optional-member omission, and the constrained extension model
/// (scalars, arrays, nested maps, reserved-key protection).
/// </summary>
public class ProblemDetailsWriterTests
{
    private static readonly IProblemDetailsWriter Writer = ProblemDetailsWriter.Default;

    [Fact(DisplayName = "Cohesion Test [Web.Results] - ProblemDetailsWriter: renders all five standard members")]
    public void Write_WithAllStandardMembers_RendersEachMember()
    {
        var problem = new ProblemDetails
        {
            Type = "https://example.com/probs/out-of-credit",
            Title = "You do not have enough credit.",
            Status = 403,
            Detail = "Your current balance is 30.",
            Instance = "/account/12345/msgs/abc"
        };

        using JsonDocument document = JsonDocument.Parse(Writer.WriteToString(problem));
        JsonElement root = document.RootElement;

        root.GetProperty("type").GetString().ShouldBe("https://example.com/probs/out-of-credit");
        root.GetProperty("title").GetString().ShouldBe("You do not have enough credit.");
        root.GetProperty("status").GetInt32().ShouldBe(403);
        root.GetProperty("detail").GetString().ShouldBe("Your current balance is 30.");
        root.GetProperty("instance").GetString().ShouldBe("/account/12345/msgs/abc");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - ProblemDetailsWriter: omits absent optional members and defaults type to about:blank")]
    public void Write_WithOnlyStatus_DefaultsTypeAndOmitsOthers()
    {
        var problem = new ProblemDetails { Status = 500 };

        using JsonDocument document = JsonDocument.Parse(Writer.WriteToString(problem));
        JsonElement root = document.RootElement;

        root.GetProperty("type").GetString().ShouldBe("about:blank");
        root.GetProperty("status").GetInt32().ShouldBe(500);
        root.TryGetProperty("title", out _).ShouldBeFalse();
        root.TryGetProperty("detail", out _).ShouldBeFalse();
        root.TryGetProperty("instance", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - ProblemDetailsWriter: FromStatus fills type, title and status from the status code")]
    public void FromStatus_ForNotFound_FillsTitleAndStatus()
    {
        ProblemDetails problem = ProblemDetails.FromStatus(HttpStatusCode.NotFound);

        using JsonDocument document = JsonDocument.Parse(Writer.WriteToString(problem));
        JsonElement root = document.RootElement;

        root.GetProperty("type").GetString().ShouldBe("about:blank");
        root.GetProperty("title").GetString().ShouldBe("Not Found");
        root.GetProperty("status").GetInt32().ShouldBe(404);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - ProblemDetailsWriter: renders scalar, array and nested-object extensions")]
    public void Write_WithExtensions_RendersConstrainedValueShapes()
    {
        var problem = new ProblemDetails { Status = 400, Title = "Bad Request" };
        problem.Extensions["traceId"] = "00-abc-def-01";
        problem.Extensions["retryable"] = false;
        problem.Extensions["attempts"] = 3;
        problem.Extensions["tags"] = new List<string> { "a", "b" };
        problem.Extensions["errors"] = new Dictionary<string, object?>
        {
            ["name"] = new List<string> { "required" }
        };

        using JsonDocument document = JsonDocument.Parse(Writer.WriteToString(problem));
        JsonElement root = document.RootElement;

        root.GetProperty("traceId").GetString().ShouldBe("00-abc-def-01");
        root.GetProperty("retryable").GetBoolean().ShouldBeFalse();
        root.GetProperty("attempts").GetInt32().ShouldBe(3);

        JsonElement tags = root.GetProperty("tags");
        tags.ValueKind.ShouldBe(JsonValueKind.Array);
        tags.GetArrayLength().ShouldBe(2);
        tags[0].GetString().ShouldBe("a");

        JsonElement errors = root.GetProperty("errors");
        errors.ValueKind.ShouldBe(JsonValueKind.Object);
        errors.GetProperty("name")[0].GetString().ShouldBe("required");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - ProblemDetailsWriter: ignores an extension that collides with a standard member")]
    public void Write_WithReservedExtensionKey_DoesNotDuplicateStandardMember()
    {
        var problem = new ProblemDetails { Status = 409, Title = "Conflict" };
        problem.Extensions["status"] = 123; // must not shadow or duplicate the standard "status"

        // JsonDocument.Parse would throw on a duplicate property, so a successful parse plus the
        // original value proves the reserved key was skipped.
        using JsonDocument document = JsonDocument.Parse(Writer.WriteToString(problem));

        document.RootElement.GetProperty("status").GetInt32().ShouldBe(409);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - ProblemDetailsWriter: null extension value renders as JSON null")]
    public void Write_WithNullExtensionValue_RendersJsonNull()
    {
        var problem = new ProblemDetails { Status = 500 };
        problem.Extensions["note"] = null;

        using JsonDocument document = JsonDocument.Parse(Writer.WriteToString(problem));

        document.RootElement.GetProperty("note").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - ProblemDetailsWriter: stream, byte and string forms all agree")]
    public void Write_AllOutputForms_ProduceEqualPayload()
    {
        ProblemDetails problem = ProblemDetails.FromStatus(HttpStatusCode.BadGateway, "upstream failed");

        string fromString = Writer.WriteToString(problem);
        string fromBytes = System.Text.Encoding.UTF8.GetString(Writer.WriteToUtf8Bytes(problem));

        using var stream = new MemoryStream();
        Writer.Write(problem, stream);
        string fromStream = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        fromBytes.ShouldBe(fromString);
        fromStream.ShouldBe(fromString);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - ProblemDetailsWriter: null argument throws")]
    public void Write_WithNullProblem_Throws()
    {
        Should.Throw<ArgumentNullException>(() => Writer.WriteToString(null!));
    }
}
