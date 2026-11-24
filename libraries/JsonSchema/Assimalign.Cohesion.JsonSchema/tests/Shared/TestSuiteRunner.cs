using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// Runs the vendored JSON Schema Test Suite (<c>TestObjects/JSON-Schema-Test-Suite</c>): the
/// validation tests, required and optional, and the annotation tests, as the suite's READMEs
/// prescribe.
/// </summary>
internal static class TestSuiteRunner
{
    private static readonly string _root = Path.Combine(AppContext.BaseDirectory, "TestObjects", "JSON-Schema-Test-Suite");
    private static readonly string _validationRoot = Path.Combine(_root, "validation", "tests");
    private static readonly string _annotationRoot = Path.Combine(_root, "annotations", "tests");

    /// <summary>Returns a row per dialect and required validation file with a test case for it.</summary>
    public static TheoryData<string, string> GetRequiredValidationRows() =>
        GetRows(_validationRoot, "tests", optional: false);

    /// <summary>Returns a row per dialect and optional validation file with a test case for it.</summary>
    public static TheoryData<string, string> GetOptionalValidationRows() =>
        GetRows(_validationRoot, "tests", optional: true);

    /// <summary>Returns a row per dialect and annotation file with a test case for it.</summary>
    public static TheoryData<string, string> GetAnnotationRows() =>
        GetRows(_annotationRoot, "suite", optional: false);

    /// <summary>
    /// Runs every applicable test of a validation file, through both <see cref="IJsonSchema.IsValid"/>
    /// and a Verbose <see cref="IJsonSchema.Evaluate"/>, and returns a line per unexpected outcome.
    /// </summary>
    public static List<string> RunValidationFile(TestSuiteDialect dialect, string file)
    {
        List<string> failures = new();
        using JsonDocument suite = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_validationRoot, file)));
        foreach (JsonElement testCase in suite.RootElement.GetProperty("tests").EnumerateArray())
        {
            if (!IsApplicable(testCase, dialect))
            {
                continue;
            }

            string caseDescription = testCase.GetProperty("description").GetString()!;
            bool formatAssertion = HasTag(testCase, "format-assertion");

            // Draft 7 made content validation an optional assertion; the optional suite tests it.
            bool contentAssertion = file == "optional/content.json";
            JsonSchemaEvaluationOptions flag = new() { FormatAssertion = formatAssertion, ContentAssertion = contentAssertion };
            JsonSchemaEvaluationOptions verbose = new() { FormatAssertion = formatAssertion, ContentAssertion = contentAssertion, OutputFormat = JsonSchemaOutputFormat.Verbose };

            JsonSchemaDocument? schema = null;
            string? loadFailure = null;
            try
            {
                schema = Load(testCase, dialect);
            }
            catch (JsonSchemaException exception)
            {
                loadFailure = $"the schema did not load: {exception.Code}: {exception.Message}";
            }

            foreach (JsonElement test in testCase.GetProperty("tests").EnumerateArray())
            {
                string testDescription = test.GetProperty("description").GetString()!;
                bool expected = test.GetProperty("valid").GetBoolean();
                string? outcome = loadFailure;
                if (schema is not null)
                {
                    JsonElement data = test.GetProperty("data");
                    try
                    {
                        bool isValid = schema.IsValid(data, flag);
                        bool evaluated = schema.Evaluate(data, verbose).IsValid;
                        if (isValid != expected)
                        {
                            outcome = $"expected {(expected ? "valid" : "invalid")}, but IsValid returned {isValid}";
                        }
                        else if (evaluated != expected)
                        {
                            outcome = $"expected {(expected ? "valid" : "invalid")}, but a Verbose evaluation returned {evaluated}";
                        }
                    }
                    catch (JsonSchemaException exception)
                    {
                        outcome = $"evaluation threw {exception.Code}: {exception.Message}";
                    }
                }

                Record(failures, dialect, file, caseDescription, testDescription, outcome);
            }
        }

        return failures;
    }

    /// <summary>
    /// Runs every applicable test of an annotation file and returns a line per assertion whose
    /// annotations differ from the expected ones.
    /// </summary>
    public static List<string> RunAnnotationFile(TestSuiteDialect dialect, string file)
    {
        List<string> failures = new();
        JsonSchemaEvaluationOptions verbose = new() { OutputFormat = JsonSchemaOutputFormat.Verbose };
        using JsonDocument suite = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_annotationRoot, file)));
        foreach (JsonElement testCase in suite.RootElement.GetProperty("suite").EnumerateArray())
        {
            if (!IsApplicable(testCase, dialect))
            {
                continue;
            }

            string caseDescription = testCase.GetProperty("description").GetString()!;
            JsonSchemaDocument? schema = null;
            string? loadFailure = null;
            try
            {
                schema = Load(testCase, dialect);
            }
            catch (JsonSchemaException exception)
            {
                loadFailure = $"the schema did not load: {exception.Code}: {exception.Message}";
            }

            int testIndex = 0;
            foreach (JsonElement test in testCase.GetProperty("tests").EnumerateArray())
            {
                string testDescription = "test " + testIndex++.ToString(System.Globalization.CultureInfo.InvariantCulture);
                List<JsonSchemaEvaluationResult> annotations = new();
                string? evaluationFailure = loadFailure;
                if (schema is not null)
                {
                    try
                    {
                        CollectAnnotations(schema.Evaluate(test.GetProperty("instance"), verbose), annotations);
                    }
                    catch (JsonSchemaException exception)
                    {
                        evaluationFailure = $"evaluation threw {exception.Code}: {exception.Message}";
                    }
                }

                foreach (JsonElement assertion in test.GetProperty("assertions").EnumerateArray())
                {
                    string location = assertion.GetProperty("location").GetString()!;
                    string keyword = assertion.GetProperty("keyword").GetString()!;
                    string? outcome = evaluationFailure ?? CompareAnnotations(annotations, location, keyword, assertion.GetProperty("expected"));
                    Record(failures, dialect, file, caseDescription, $"{testDescription} {keyword} at '{location}'", outcome);
                }
            }
        }

        return failures;
    }

    private static TheoryData<string, string> GetRows(string root, string casesProperty, bool optional)
    {
        TheoryData<string, string> rows = new();
        string directory = optional ? Path.Combine(root, "optional") : root;
        string[] files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        foreach (TestSuiteDialect dialect in TestSuiteDialect.All)
        {
            foreach (string path in files)
            {
                using JsonDocument suite = JsonDocument.Parse(File.ReadAllBytes(path));
                if (suite.RootElement.GetProperty(casesProperty).EnumerateArray().Any(testCase => IsApplicable(testCase, dialect)))
                {
                    rows.Add(dialect.Name, Path.GetRelativePath(root, path).Replace('\\', '/'));
                }
            }
        }

        return rows;
    }

    private static bool IsApplicable(JsonElement testCase, TestSuiteDialect dialect)
    {
        string? compatibility = testCase.TryGetProperty("compatibility", out JsonElement value) ? value.GetString() : null;
        return dialect.IsCompatible(compatibility);
    }

    private static bool HasTag(JsonElement testCase, string tag)
    {
        if (testCase.TryGetProperty("tags", out JsonElement tags))
        {
            foreach (JsonElement item in tags.EnumerateArray())
            {
                if (item.ValueEquals(tag))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Loads a test case's schema in a registry of its own, after its external schemas, each under its
    /// retrieval URI. An external schema whose <c>$schema</c> names another one loads after it.
    /// </summary>
    private static JsonSchemaDocument Load(JsonElement testCase, TestSuiteDialect dialect)
    {
        JsonSchemaRegistry registry = new();
        if (testCase.TryGetProperty("externalSchemas", out JsonElement externalSchemas))
        {
            List<JsonProperty> pending = externalSchemas.EnumerateObject().ToList();
            while (pending.Count > 0)
            {
                List<JsonProperty> deferred = new();
                JsonSchemaException? lastFailure = null;
                foreach (JsonProperty entry in pending)
                {
                    try
                    {
                        registry.Register(new Uri(entry.Name, UriKind.Absolute), entry.Value, dialect.Dialect);
                    }
                    catch (JsonSchemaException exception) when (exception.Code == JsonSchemaErrorCode.UnknownDialect)
                    {
                        deferred.Add(entry);
                        lastFailure = exception;
                    }
                }

                if (deferred.Count == pending.Count)
                {
                    throw lastFailure!;
                }

                pending = deferred;
            }
        }

        return JsonSchemaDocument.Create(testCase.GetProperty("schema"), new JsonSchemaDocumentOptions
        {
            DefaultDialect = dialect.Dialect,
            Registry = registry,
        });
    }

    private static void CollectAnnotations(JsonSchemaEvaluationResult unit, List<JsonSchemaEvaluationResult> annotations)
    {
        if (unit.Annotation is not null)
        {
            annotations.Add(unit);
        }

        foreach (JsonSchemaEvaluationResult child in unit.Annotations)
        {
            CollectAnnotations(child, annotations);
        }

        foreach (JsonSchemaEvaluationResult child in unit.Errors)
        {
            CollectAnnotations(child, annotations);
        }
    }

    /// <summary>
    /// Compares the annotations a keyword produced at an instance location with the expected map from
    /// schema location (a URI fragment of the document) to annotation value.
    /// </summary>
    private static string? CompareAnnotations(List<JsonSchemaEvaluationResult> annotations, string location, string keyword, JsonElement expected)
    {
        Dictionary<string, JsonElement> actual = new(StringComparer.Ordinal);
        foreach (JsonSchemaEvaluationResult unit in annotations)
        {
            if (unit.Keyword == keyword && unit.InstanceLocation.Value == location)
            {
                actual[unit.Schema!.Location.Value!] = unit.Annotation!.Value;
            }
        }

        Dictionary<string, JsonElement> wanted = new(StringComparer.Ordinal);
        foreach (JsonProperty entry in expected.EnumerateObject())
        {
            wanted[Uri.UnescapeDataString(entry.Name.TrimStart('#'))] = entry.Value;
        }

        string Describe(Dictionary<string, JsonElement> map) =>
            map.Count == 0 ? "none" : string.Join(", ", map.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"#{pair.Key}: {pair.Value.GetRawText()}"));

        bool matches = actual.Count == wanted.Count
            && wanted.All(pair => actual.TryGetValue(pair.Key, out JsonElement value) && JsonElement.DeepEquals(value, pair.Value));
        return matches ? null : $"expected annotations {Describe(wanted)}, but got {Describe(actual)}";
    }

    private static void Record(List<string> failures, TestSuiteDialect dialect, string file, string testCase, string test, string? outcome)
    {
        TestSuiteExclusion? exclusion = TestSuiteExclusion.Find(dialect, file, testCase, test);
        if (outcome is not null && exclusion is null)
        {
            failures.Add($"{testCase} / {test}: {outcome}");
        }
        else if (outcome is null && exclusion is not null)
        {
            failures.Add($"{testCase} / {test}: passes, so its exclusion ({exclusion.Reason}) must be removed");
        }
    }
}
