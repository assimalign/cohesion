using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Shouldly;

using Xunit;

using Assimalign.Cohesion;

namespace Assimalign.Cohesion.SourceGeneration.Tests;

/// <summary>
/// Two-stage generator-driver coverage for component integrations declared in referenced assembly
/// metadata.
/// </summary>
public class ComponentIntegrationGeneratorTests
{
    private const string SeamSource = """
        #nullable enable
        using System;

        namespace Assimalign.Cohesion.DependencyInjection;

        public interface IServiceProviderBuilder
        {
        }

        public static class SeamExtensions
        {
            extension(IServiceProviderBuilder seam)
            {
                public IServiceProviderBuilder AddSingleton<T>(Func<IServiceProvider, T> factory)
                    where T : class
                    => seam;

                public IServiceProviderBuilder AddSingleton<T>(T instance)
                    where T : class
                    => seam;
            }
        }
        """;

    /// <summary>
    /// <see cref="SeamSource"/>'s shape with a seam that records what each producer registration
    /// produces, so an executed consumer can report what the projected verbs registered.
    /// </summary>
    private const string RecordingSeamSource = """
        #nullable enable
        using System;
        using System.Collections.Generic;

        namespace Assimalign.Cohesion.DependencyInjection;

        public interface IServiceProviderBuilder
        {
        }

        public sealed class RecordingSeam : IServiceProviderBuilder
        {
            public List<object> Products { get; } = new();

            public int ProducerInvocations { get; set; } = 1;
        }

        public static class SeamExtensions
        {
            extension(IServiceProviderBuilder seam)
            {
                public IServiceProviderBuilder AddSingleton<T>(Func<IServiceProvider, T> factory)
                    where T : class
                {
                    var recordingSeam = (RecordingSeam)seam;
                    for (int i = 0; i < recordingSeam.ProducerInvocations; i++)
                    {
                        recordingSeam.Products.Add(factory(null!));
                    }
                    return seam;
                }

                public IServiceProviderBuilder AddSingleton<T>(T instance)
                    where T : class
                    => seam;
            }
        }
        """;

    private static readonly IReadOnlyList<MetadataReference> _platformReferences =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.Length > 0)
            .Where(path => !string.Equals(
                Path.GetFileNameWithoutExtension(path),
                "Assimalign.Cohesion.Core",
                StringComparison.Ordinal))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: seam referenced emits a callable verb")]
    public void Generator_SeamReferenced_EmitsCallableVerb()
    {
        MetadataReference contributor = CompileReference(
            "Test.Contributor",
            ContributorSource(
                """
                public static Func<IServiceProvider, IThing> CreateThing(string name)
                    => _ => new Thing();
                """));
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(
            """
            using Test.Contributor;
            using Assimalign.Cohesion.DependencyInjection;

            internal static class Consumer
            {
                internal static IServiceProviderBuilder Configure(IServiceProviderBuilder seam) => seam.AddProjected("configured");
            }
            """,
            new[] { contributor },
            new[] { seam });

        string generated = GeneratedText(result);
        generated.ShouldContain("extension(global::Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder builder)", Case.Sensitive);
        generated.ShouldContain("public global::Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder AddProjected(", Case.Sensitive);
        AssertNoErrors(result.Compilation);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: seam absent emits nothing and reports discoverability info")]
    public void Generator_SeamAbsent_EmitsNothingAndReportsInfo()
    {
        MetadataReference contributor = CompileReference(
            "Test.Contributor",
            ContributorSource(
                """
                public static Func<IServiceProvider, IThing> CreateThing()
                    => _ => new Thing();
                """));

        GeneratorResult result = Run(string.Empty, new[] { contributor });

        result.RunResult.GeneratedTrees.ShouldBeEmpty();
        result.RunResult.Diagnostics.ShouldContain(
            diagnostic => diagnostic.Id == "COHCMP0005"
                && diagnostic.Severity == DiagnosticSeverity.Info);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: every public static factory overload is projected")]
    public void Generator_FactoryOverloads_ProjectsEveryPublicStaticOverload()
    {
        MetadataReference contributor = CompileReference(
            "Test.Contributor",
            ContributorSource(
                """
                public static Func<IServiceProvider, IThing> CreateThing(string name)
                    => _ => new Thing();

                public static Func<IServiceProvider, IThing> CreateThing(int count)
                    => _ => new Thing();
                """));
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(string.Empty, new[] { contributor }, new[] { seam });
        string generated = GeneratedText(result);

        CountOccurrences(generated, "public global::Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder AddProjected(").ShouldBe(2);
        generated.ShouldContain("global::System.String @name", Case.Sensitive);
        generated.ShouldContain("global::System.Int32 @count", Case.Sensitive);
        AssertNoErrors(result.Compilation);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: optional parameter defaults are preserved")]
    public void Generator_OptionalParameter_PreservesDefault()
    {
        MetadataReference contributor = CompileReference(
            "Test.Contributor",
            ContributorSource(
                """
                public static Func<IServiceProvider, IThing> CreateThing(int retries = 3)
                    => _ => new Thing();
                """));
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(string.Empty, new[] { contributor }, new[] { seam });
        string generated = GeneratedText(result);

        generated.ShouldContain("global::System.Int32 @retries = 3", Case.Sensitive);
        AssertNoErrors(result.Compilation);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: disposable instance warns while a producer does not")]
    public void Generator_DisposableProduct_AppliesProducerRule()
    {
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);
        const string disposableType = """
            public sealed class DisposableThing : IThing, IDisposable
            {
                public void Dispose()
                {
                }
            }
            """;

        MetadataReference instanceContributor = CompileReference(
            "Test.InstanceContributor",
            ContributorSource(
                """
                public static DisposableThing CreateThing()
                    => new DisposableThing();
                """,
                contributorNamespace: "Test.InstanceContributor",
                additionalTypes: disposableType));

        GeneratorResult instanceResult = Run(
            string.Empty,
            new[] { instanceContributor },
            new[] { seam });

        instanceResult.RunResult.Diagnostics.ShouldContain(
            diagnostic => diagnostic.Id == "COHCMP0007"
                && diagnostic.Severity == DiagnosticSeverity.Warning);
        AssertNoErrors(instanceResult.Compilation);

        MetadataReference producerContributor = CompileReference(
            "Test.ProducerContributor",
            ContributorSource(
                """
                public static Func<IServiceProvider, DisposableThing> CreateThing()
                    => _ => new DisposableThing();
                """,
                contributorNamespace: "Test.ProducerContributor",
                additionalTypes: disposableType));

        GeneratorResult producerResult = Run(
            string.Empty,
            new[] { producerContributor },
            new[] { seam });

        producerResult.RunResult.Diagnostics.ShouldNotContain(
            diagnostic => diagnostic.Id == "COHCMP0007");
        AssertNoErrors(producerResult.Compilation);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: duplicate projection keeps the deterministic first verb")]
    public void Generator_DuplicateProjection_ReportsWarningAndEmitsOneVerb()
    {
        MetadataReference firstContributor = CompileReference(
            "A.Contributor",
            ContributorSource(
                """
                public static Func<IServiceProvider, IThing> CreateThing()
                    => _ => new Thing();
                """,
                contributorNamespace: "Test.FirstContributor"));
        MetadataReference secondContributor = CompileReference(
            "B.Contributor",
            ContributorSource(
                """
                public static Func<IServiceProvider, IThing> CreateThing()
                    => _ => new Thing();
                """,
                contributorNamespace: "Test.SecondContributor"));
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(
            string.Empty,
            new[] { secondContributor, firstContributor },
            new[] { seam });

        result.RunResult.Diagnostics.ShouldContain(
            diagnostic => diagnostic.Id == "COHCMP0004"
                && diagnostic.Severity == DiagnosticSeverity.Warning);
        CountOccurrences(
            GeneratedText(result),
            "public global::Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder AddProjected(").ShouldBe(1);
        AssertNoErrors(result.Compilation);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: ambiguous seam metadata name still passes the gate")]
    public void Generator_AmbiguousSeamMetadataName_EmitsWithoutCrashing()
    {
        MetadataReference contributor = CompileReference(
            "Test.Contributor",
            ContributorSource(
                """
                public static Func<IServiceProvider, IThing> CreateThing()
                    => _ => new Thing();
                """));
        MetadataReference firstSeam = CompileReference(
            "Test.Seam.One",
            SeamSource,
            includeCore: false);
        MetadataReference secondSeam = CompileReference(
            "Test.Seam.Two",
            SeamSource,
            includeCore: false);

        GeneratorResult result = Run(
            string.Empty,
            new[] { contributor },
            new[] { firstSeam, secondSeam });

        result.RunResult.GeneratedTrees.Length.ShouldBe(1);
        GeneratedText(result).ShouldContain(
            "extension(global::Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder builder)",
            Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: generic factory method reports unsupported shape")]
    public void Generator_GenericFactoryMethod_ReportsUnsupportedShapeAndEmitsNothing()
    {
        MetadataReference contributor = CompileReference(
            "Test.Contributor",
            ContributorSource(
                """
                public static Func<IServiceProvider, T> CreateThing<T>()
                    where T : class, IThing
                    => _ => default!;
                """));
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(string.Empty, new[] { contributor }, new[] { seam });

        result.RunResult.GeneratedTrees.ShouldBeEmpty();
        result.RunResult.Diagnostics.ShouldContain(
            diagnostic => diagnostic.Id == "COHCMP0003"
                && diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: contract controls explicit target type argument")]
    public void Generator_Contract_ControlsExplicitTargetTypeArgument()
    {
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);
        MetadataReference explicitContributor = CompileReference(
            "Test.ContractContributor",
            ContributorSource(
                """
                public static Func<IServiceProvider, IThing> CreateThing()
                    => _ => new Thing();
                """,
                contributorNamespace: "Test.ContractContributor"));

        GeneratorResult explicitResult = Run(
            string.Empty,
            new[] { explicitContributor },
            new[] { seam });
        string explicitGenerated = GeneratedText(explicitResult);

        explicitGenerated.ShouldContain(
            "builder.AddSingleton<global::Test.ContractContributor.IThing>(",
            Case.Sensitive);
        AssertNoErrors(explicitResult.Compilation);

        MetadataReference inferredContributor = CompileReference(
            "Test.InferredContributor",
            ContributorSource(
                """
                public static Func<IServiceProvider, IThing> CreateThing()
                    => _ => new Thing();
                """,
                includeContract: false,
                contributorNamespace: "Test.InferredContributor"));

        GeneratorResult inferredResult = Run(
            string.Empty,
            new[] { inferredContributor },
            new[] { seam });
        string inferredGenerated = GeneratedText(inferredResult);

        inferredGenerated.ShouldContain("builder.AddSingleton(", Case.Sensitive);
        inferredGenerated.ShouldNotContain("builder.AddSingleton<", Case.Sensitive);
        AssertNoErrors(inferredResult.Compilation);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: assembly-local internal declarations work without a Core reference")]
    public void Generator_InternalAttributeWithoutCoreReference_EmitsCallableVerb()
    {
        MetadataReference contributor = CompileReference(
            "Test.Contributor",
            ContributorSource(
                """
                public static Func<IServiceProvider, IThing> CreateThing()
                    => _ => new Thing();
                """), includeCore: false);
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(
            string.Empty,
            new[] { contributor },
            new[] { seam },
            includeCore: false);

        result.RunResult.GeneratedTrees.Length.ShouldBe(1);
        result.RunResult.Diagnostics.ShouldBeEmpty();
        AssertNoErrors(result.Compilation);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: Core does not expose the opt-in attribute")]
    public void Generator_WithoutOptIn_DoesNotExposeAttribute()
    {
        GeneratorResult result = Run(string.Empty, Array.Empty<MetadataReference>());
        IAssemblySymbol core = result.Compilation.SourceModule.ReferencedAssemblySymbols
            .Single(assembly => assembly.Name == "Assimalign.Cohesion.Core");
        core.GetTypeByMetadataName("Assimalign.Cohesion.ComponentIntegrationAttribute").ShouldBeNull();
        result.RunResult.GeneratedTrees.ShouldBeEmpty();
        result.RunResult.Diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: malformed attribute argument types report a schema diagnostic")]
    public void Generator_MalformedAttributeArgumentTypes_ReportsSchemaDiagnostic()
    {
        const string malformedContributorSource = """
            #nullable enable
            using System;

            [assembly: Assimalign.Cohesion.ComponentIntegration(
                "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
                "AddSingleton",
                typeof(Test.MalformedContributor.Components),
                "CreateThing")]

            namespace Assimalign.Cohesion
            {
                [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
                public sealed class ComponentIntegrationAttribute : Attribute
                {
                    public ComponentIntegrationAttribute(
                        object targetTypeName,
                        object targetMethodName,
                        Type factoryType,
                        object factoryMethodName)
                    {
                    }
                }
            }

            namespace Test.MalformedContributor
            {
                public interface IThing
                {
                }

                public sealed class Thing : IThing
                {
                }

                public static class Components
                {
                    public static Type CoreAnchor => typeof(global::Assimalign.Cohesion.AppEnvironment);

                    public static Func<IServiceProvider, IThing> CreateThing()
                        => _ => new Thing();
                }
            }
            """;

        MetadataReference contributor = CompileReference(
            "Test.MalformedContributor",
            malformedContributorSource);
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(string.Empty, new[] { contributor }, new[] { seam });

        result.RunResult.GeneratedTrees.ShouldBeEmpty();
        result.RunResult.Diagnostics.ShouldContain(
            diagnostic => diagnostic.Id == "COHCMP0001"
                && diagnostic.Severity == DiagnosticSeverity.Warning);

        const string malformedNamedArgumentSource = """
            #nullable enable
            using System;

            [assembly: Assimalign.Cohesion.ComponentIntegration(
                "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
                "AddSingleton",
                typeof(Test.MalformedNamedArgument.Components),
                "CreateThing",
                Verb = "AddProjected",
                Contract = typeof(Test.MalformedNamedArgument.IThing))]

            namespace Assimalign.Cohesion
            {
                [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
                public sealed class ComponentIntegrationAttribute : Attribute
                {
                    public ComponentIntegrationAttribute(
                        string targetTypeName,
                        string targetMethodName,
                        Type factoryType,
                        string factoryMethodName)
                    {
                    }

                    public object? Verb { get; set; }

                    public object? Contract { get; set; }
                }
            }

            namespace Test.MalformedNamedArgument
            {
                public interface IThing
                {
                }

                public sealed class Thing : IThing
                {
                }

                public static class Components
                {
                    public static Type CoreAnchor => typeof(global::Assimalign.Cohesion.AppEnvironment);

                    public static Func<IServiceProvider, IThing> CreateThing()
                        => _ => new Thing();
                }
            }
            """;

        MetadataReference namedArgumentContributor = CompileReference(
            "Test.MalformedNamedArgument",
            malformedNamedArgumentSource);
        GeneratorResult namedArgumentResult = Run(
            string.Empty,
            new[] { namedArgumentContributor },
            new[] { seam });

        namedArgumentResult.RunResult.GeneratedTrees.ShouldBeEmpty();
        namedArgumentResult.RunResult.Diagnostics.ShouldContain(
            diagnostic => diagnostic.Id == "COHCMP0001"
                && diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: negative enum defaults remain valid C#")]
    public void Generator_NegativeEnumDefault_EmitsParenthesizedConstant()
    {
        MetadataReference contributor = CompileReference(
            "Test.Contributor",
            ContributorSource(
                """
                public static Func<IServiceProvider, IThing> CreateThing(
                    Mode mode = Mode.Disabled,
                    long floor = long.MinValue)
                    => _ => new Thing();
                """,
                additionalTypes: "public enum Mode { Disabled = -1 }"));
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(string.Empty, new[] { contributor }, new[] { seam });

        GeneratedText(result).ShouldContain(
            "(global::Test.Contributor.Mode)(-1)",
            Case.Sensitive);
        GeneratedText(result).ShouldContain("-9223372036854775808L", Case.Sensitive);
        AssertNoErrors(result.Compilation);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: receiver name avoids every copied parameter")]
    public void Generator_ReceiverNameCollision_ChoosesUnusedIdentifier()
    {
        MetadataReference contributor = CompileReference(
            "Test.Contributor",
            ContributorSource(
                """
                public static Func<IServiceProvider, IThing> CreateThing(
                    string builder,
                    int receiver,
                    bool componentBuilder)
                    => _ => new Thing();
                """));
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(string.Empty, new[] { contributor }, new[] { seam });

        GeneratedText(result).ShouldContain(
            "extension(global::Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder componentBuilder2)",
            Case.Sensitive);
        AssertNoErrors(result.Compilation);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: keyword member names are escaped")]
    public void Generator_KeywordMemberNames_EmitCallableIdentifiers()
    {
        const string contributorSource = """
            #nullable enable
            using System;
            using Assimalign.Cohesion;

            [assembly: ComponentIntegration(
                ComponentIntegrationTarget.ServiceProviderBuilder,
                ComponentIntegrationMethod.AddSingleton,
                typeof(Test.KeywordContributor.Components),
                nameof(Test.KeywordContributor.Components.@new),
                ComponentIntegrationTemplate.Factory,
                Verb = "return",
                Contract = typeof(Test.KeywordContributor.IThing))]

            namespace Test.KeywordContributor;

            public interface IThing
            {
            }

            public sealed class Thing : IThing
            {
            }

            public static class Components
            {
                public static Func<IServiceProvider, IThing> @new() => _ => new Thing();
            }
            """;
        const string keywordSeamSource = """
            #nullable enable
            using System;

            namespace Assimalign.Cohesion.DependencyInjection;

            public interface IServiceProviderBuilder
            {
            }

            public static class SeamExtensions
            {
                extension(IServiceProviderBuilder seam)
                {
                    public IServiceProviderBuilder AddSingleton<T>(Func<IServiceProvider, T> factory)
                        where T : class
                        => seam;
                }
            }
            """;
        MetadataReference contributor = CompileReference("Test.KeywordContributor", contributorSource);
        MetadataReference seam = CompileReference("Test.Seam", keywordSeamSource, includeCore: false);

        GeneratorResult result = Run(
            """
            using Test.KeywordContributor;
            using Assimalign.Cohesion.DependencyInjection;

            internal static class Consumer
            {
                internal static IServiceProviderBuilder Configure(IServiceProviderBuilder seam) => seam.@return();
            }
            """,
            new[] { contributor },
            new[] { seam });
        string generated = GeneratedText(result);

        generated.ShouldContain("builder.AddSingleton<", Case.Sensitive);
        generated.ShouldContain("Components.@new()", Case.Sensitive);
        generated.ShouldContain(" @return(", Case.Sensitive);
        AssertNoErrors(result.Compilation);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: C# 13 reports language diagnostic and emits nothing")]
    public void Generator_CSharp13_ReportsUnsupportedLanguageVersion()
    {
        MetadataReference contributor = CompileReference(
            "Test.Contributor",
            ContributorSource(
                """
                public static Func<IServiceProvider, IThing> CreateThing()
                    => _ => new Thing();
                """));
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(
            string.Empty,
            new[] { contributor },
            new[] { seam },
            languageVersion: LanguageVersion.CSharp13);

        result.RunResult.GeneratedTrees.ShouldBeEmpty();
        result.RunResult.Diagnostics.ShouldContain(
            diagnostic => diagnostic.Id == "COHCMP0006"
                && diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: instance factory method emits a callable builder-template verb")]
    public void Generator_InstanceFactoryMethod_EmitsCallableBuilderTemplateVerb()
    {
        MetadataReference contributor = CompileReference(
            "Test.BuilderContributor",
            BuilderContributorSource());
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(
            """
            using Test.BuilderContributor;
            using Assimalign.Cohesion.DependencyInjection;

            internal static class Consumer
            {
                internal static IServiceProviderBuilder Configure(IServiceProviderBuilder seam) => seam.AddProjected(builder => { });
            }
            """,
            new[] { contributor },
            new[] { seam });
        string generated = GeneratedText(result);

        generated.ShouldContain(
            "public global::Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder AddProjected(global::System.Action<global::Test.BuilderContributor.ThingBuilder>? @configure = null)",
            Case.Sensitive);
        generated.ShouldContain(
            "var cohesionComponentFactory = new global::Test.BuilderContributor.ThingBuilder();",
            Case.Sensitive);
        generated.ShouldContain("@configure?.Invoke(cohesionComponentFactory);", Case.Sensitive);
        generated.ShouldContain(
            "var cohesionComponent = cohesionComponentFactory.Build();",
            Case.Sensitive);
        generated.ShouldContain(
            "builder.AddSingleton<global::Test.BuilderContributor.IThing>(_ => cohesionComponent);",
            Case.Sensitive);
        generated.ShouldNotContain("(global::System.Func<", Case.Sensitive);
        AssertNoErrors(result.Compilation);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: mixed static and instance factory method group reports unsupported shape")]
    public void Generator_MixedFactoryMethodGroup_ReportsUnsupportedShapeAndEmitsNothing()
    {
        MetadataReference contributor = CompileReference(
            "Test.MixedBuilderContributor",
            BuilderContributorSource(
                contributorNamespace: "Test.MixedBuilderContributor",
                builderMembers: """
                    public IThing Build()
                        => new Thing();

                    public static IThing Build(string name)
                        => new Thing();
                    """));
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(string.Empty, new[] { contributor }, new[] { seam });

        result.RunResult.GeneratedTrees.ShouldBeEmpty();
        result.RunResult.Diagnostics.ShouldContain(
            diagnostic => diagnostic.Id == "COHCMP0003"
                && diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: builder without public parameterless constructor reports unsupported shape")]
    public void Generator_BuilderWithoutPublicParameterlessConstructor_ReportsUnsupportedShapeAndEmitsNothing()
    {
        MetadataReference contributor = CompileReference(
            "Test.ConstructorBuilderContributor",
            BuilderContributorSource(
                contributorNamespace: "Test.ConstructorBuilderContributor",
                builderMembers: """
                    public ThingBuilder(string name)
                    {
                    }

                    public IThing Build()
                        => new Thing();
                    """));
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(string.Empty, new[] { contributor }, new[] { seam });

        result.RunResult.GeneratedTrees.ShouldBeEmpty();
        result.RunResult.Diagnostics.ShouldContain(
            diagnostic => diagnostic.Id == "COHCMP0003"
                && diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: instance factory methods with parameters report unsupported shape")]
    public void Generator_ParameterizedInstanceFactoryMethodsOnly_ReportUnsupportedShapeAndEmitNothing()
    {
        MetadataReference contributor = CompileReference(
            "Test.ParameterizedBuilderContributor",
            BuilderContributorSource(
                contributorNamespace: "Test.ParameterizedBuilderContributor",
                builderMembers: """
                    public IThing Build(string name)
                        => new Thing();
                    """));
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(string.Empty, new[] { contributor }, new[] { seam });

        result.RunResult.GeneratedTrees.ShouldBeEmpty();
        result.RunResult.Diagnostics.ShouldContain(
            diagnostic => diagnostic.Id == "COHCMP0003"
                && diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: disposable builder product is registered as a producer without a disposal warning")]
    public void Generator_DisposableBuilderProduct_EmitsVerbWithoutDisposableInstanceWarning()
    {
        MetadataReference contributor = CompileReference(
            "Test.DisposableBuilderContributor",
            BuilderContributorSource(
                contributorNamespace: "Test.DisposableBuilderContributor",
                builderMembers: """
                    public DisposableThing Build()
                        => new DisposableThing();
                    """,
                additionalTypes: """
                    public sealed class DisposableThing : IThing, IDisposable
                    {
                        public void Dispose()
                        {
                        }
                    }
                    """));
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(string.Empty, new[] { contributor }, new[] { seam });

        result.RunResult.Diagnostics.ShouldNotContain(
            diagnostic => diagnostic.Id == "COHCMP0007");
        GeneratedText(result).ShouldContain(
            "public global::Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder AddProjected(",
            Case.Sensitive);
        AssertNoErrors(result.Compilation);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: builder-template verb takes an optional configure callback")]
    public void Generator_BuilderTemplateVerb_EmitsOptionalConfigureCallback()
    {
        MetadataReference contributor = CompileReference(
            "Test.OptionalBuilderContributor",
            BuilderContributorSource(contributorNamespace: "Test.OptionalBuilderContributor"));
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(
            """
            using Test.OptionalBuilderContributor;
            using Assimalign.Cohesion.DependencyInjection;

            internal static class Consumer
            {
                internal static IServiceProviderBuilder Bare(IServiceProviderBuilder seam) => seam.AddProjected();

                internal static IServiceProviderBuilder ExplicitNull(IServiceProviderBuilder seam) => seam.AddProjected(null);

                internal static IServiceProviderBuilder Configured(IServiceProviderBuilder seam) => seam.AddProjected(builder => { });
            }
            """,
            new[] { contributor },
            new[] { seam });
        string generated = GeneratedText(result);

        generated.ShouldContain(
            "AddProjected(global::System.Action<global::Test.OptionalBuilderContributor.ThingBuilder>? @configure = null)",
            Case.Sensitive);
        generated.ShouldContain("@configure?.Invoke(cohesionComponentFactory);", Case.Sensitive);
        AssertNoErrors(result.Compilation);
    }

    [Theory(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: builder-template verb supports provider-aware configuration")]
    [InlineData(true)]
    [InlineData(false)]
    public void Generator_BuilderTemplateVerbWithServiceProvider_EmitsCallableDeferredComposition(bool includeContract)
    {
        // Arrange
        string contributorSource = BuilderContributorSource(includeContract: includeContract);
        MetadataReference contributor = CompileReference("Test.BuilderContributor", contributorSource);
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        // Act
        GeneratorResult result = Run(
            """
            using System;
            using Test.BuilderContributor;
            using Assimalign.Cohesion.DependencyInjection;

            internal static class Consumer
            {
                internal static IServiceProviderBuilder Configure(IServiceProviderBuilder seam) => seam.AddProjected((provider, builder) =>
                {
                    _ = provider.GetService(typeof(IThing));
                });

                internal static IServiceProviderBuilder ConfigureMethodGroup(IServiceProviderBuilder seam) => seam.AddProjected(ConfigureBuilder);

                private static void ConfigureBuilder(IServiceProvider provider, ThingBuilder builder) { }
            }
            """,
            new[] { contributor },
            new[] { seam });

        // Assert
        string generated = GeneratedText(result);
        generated.ShouldContain(
            "AddProjected(global::System.Action<global::System.IServiceProvider, global::Test.BuilderContributor.ThingBuilder> @configure)",
            Case.Sensitive);
        generated.ShouldContain("@configure(cohesionServiceProvider, cohesionComponentFactory);", Case.Sensitive);
        generated.ShouldContain("return cohesionComponentFactory.Build();", Case.Sensitive);
        result.RunResult.Diagnostics.ShouldBeEmpty();
        AssertNoErrors(result.Compilation);
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: builder-template verb called bare registers the builder's default product")]
    public void Generator_BuilderTemplateVerbCalledBare_RegistersDefaultProduct()
    {
        string[] registered = ExecuteNamedThingConsumer(
            """
            seam.AddProjected();
            seam.AddProjected(null);
            """);

        registered.ShouldBe(new[] { "default", "default" });
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: builder-template verb with a callback registers the configured product")]
    public void Generator_BuilderTemplateVerbWithCallback_RegistersConfiguredProduct()
    {
        string[] registered = ExecuteNamedThingConsumer(
            """
            seam.AddProjected(builder => builder.Name = "configured");
            """);

        registered.ShouldBe(new[] { "configured" });
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: provider callback uses a fresh builder on every producer invocation")]
    public void Generator_ProviderCallbackInvokedRepeatedly_BuildsFreshProducts()
    {
        // Arrange / Act
        string[] registered = ExecuteNamedThingConsumer(
            """
            seam.ProducerInvocations = 2;
            int configureCalls = 0;
            seam.AddProjected((provider, builder) => builder.Name += (++configureCalls).ToString());
            """);

        // Assert
        registered.ShouldBe(new[] { "default1", "default2" });
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: custom block executes with expanded placeholders")]
    public void Generator_CustomBody_RegistersConfiguredProduct()
    {
        string[] registered = ExecuteNamedThingConsumer(
            """seam.AddProjected(builder => builder.Name = "custom");""",
            useCustomTemplate: true);
        registered.ShouldBe(new[] { "custom" });
    }

    [Theory(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: invalid custom declarations are diagnosed")]
    [InlineData("$unknown$;", "")]
    [InlineData("return $receiver$", "")]
    [InlineData("return $receiver$;", "int broken =")]
    public void Generator_InvalidCustomBodyOrSignature_ReportsDiagnostic(string statements, string signature)
    {
        string source = ContributorSource("public static IThing CreateThing() => new Thing();")
            .Replace("ComponentIntegrationTemplate.Factory,", $$"""
                ComponentIntegrationTemplate.Custom,
                Signature = "{{signature}}",
                Body = "{ {{statements}} }",
                """);
        MetadataReference contributor = CompileReference("Test.CustomContributor", source);
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);
        GeneratorResult result = Run(string.Empty, new[] { contributor }, new[] { seam });
        result.RunResult.GeneratedTrees.ShouldBeEmpty();
        result.RunResult.Diagnostics.ShouldContain(diagnostic => diagnostic.Id == "COHCMP0003");
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: one builder declaration emits only its selected signature")]
    public void Generator_SingleBuilderDeclaration_EmitsOnlySelectedSignature()
    {
        var root = CSharpSyntaxTree.ParseText(BuilderContributorSource()).GetCompilationUnitRoot();
        string source = root.RemoveNode(root.AttributeLists[1], SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString();
        MetadataReference contributor = CompileReference("Test.BuilderContributor", source);
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);
        GeneratorResult result = Run(string.Empty, new[] { contributor }, new[] { seam });
        CountOccurrences(GeneratedText(result), " AddProjected(").ShouldBe(1);
        GeneratedText(result).ShouldNotContain("Action<global::System.IServiceProvider,", Case.Sensitive);
        AssertNoErrors(result.Compilation);
    }

    [Theory(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: explicit registration method selects the sink")]
    [InlineData("AddSingleton")]
    [InlineData("AddScoped")]
    [InlineData("AddTransient")]
    public void Generator_ExplicitTargetMethod_UsesSelectedSink(string targetMethod)
    {
        string source = BuilderContributorSource().Replace("ComponentIntegrationMethod.AddSingleton", "ComponentIntegrationMethod." + targetMethod);
        MetadataReference contributor = CompileReference("Test.BuilderContributor", source);
        MetadataReference seam = CompileReference("Test.Seam", SeamSource.Replace("AddSingleton", targetMethod), includeCore: false);
        GeneratorResult result = Run(string.Empty, new[] { contributor }, new[] { seam });
        GeneratedText(result).ShouldContain("builder." + targetMethod + "<", Case.Sensitive);
        result.RunResult.Diagnostics.ShouldBeEmpty();
        AssertNoErrors(result.Compilation);
    }

    [Theory(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: unknown catalog values are rejected")]
    [InlineData("ComponentIntegrationTarget.ServiceProviderBuilder", "(ComponentIntegrationTarget)999")]
    [InlineData("ComponentIntegrationMethod.AddSingleton", "(ComponentIntegrationMethod)999")]
    [InlineData("ComponentIntegrationTemplate.Factory", "(ComponentIntegrationTemplate)999")]
    public void Generator_UnknownCatalogValue_ReportsSchemaDiagnostic(string original, string replacement)
    {
        string source = ContributorSource("public static IThing CreateThing() => new Thing();").Replace(original, replacement);
        MetadataReference contributor = CompileReference("Test.InvalidCatalogContributor", source);
        GeneratorResult result = Run(string.Empty, new[] { contributor });
        result.RunResult.GeneratedTrees.ShouldBeEmpty();
        result.RunResult.Diagnostics.ShouldContain(diagnostic => diagnostic.Id == "COHCMP0001");
    }

    [Fact(DisplayName = "Cohesion Test [SourceGeneration.ComponentModel] - Generator: builder-template verb emits the complete eager composition body")]
    public void Generator_BuilderTemplateVerb_EmitsCompleteEagerCompositionBody()
    {
        MetadataReference contributor = CompileReference(
            "Test.EndToEndBuilderContributor",
            BuilderContributorSource(
                contributorNamespace: "Test.EndToEndBuilderContributor",
                verb: "AddEndToEnd",
                builderMembers: """
                    public ThingBuilder Configure(string name)
                        => this;

                    public IThing Build()
                        => new Thing();
                    """));
        MetadataReference seam = CompileReference("Test.Seam", SeamSource, includeCore: false);

        GeneratorResult result = Run(
            """
            using Test.EndToEndBuilderContributor;
            using Assimalign.Cohesion.DependencyInjection;

            internal static class Consumer
            {
                internal static IServiceProviderBuilder Configure(IServiceProviderBuilder seam) =>
                    seam.AddEndToEnd(builder => builder.Configure("configured"));
            }
            """,
            new[] { contributor },
            new[] { seam });
        string generated = NormalizeLineEndings(GeneratedText(result));

        // The expected block is normalized BEFORE indenting: on a CRLF checkout (the Windows runners)
        // the literal's blank lines are "\r", which Indent would otherwise treat as text and indent,
        // and the whole block would be compared against the LF-normalized generator output.
        generated.ShouldContain(
            Indent(NormalizeLineEndings(
                """
                    public global::Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder AddEndToEnd(global::System.Action<global::Test.EndToEndBuilderContributor.ThingBuilder>? @configure = null)
                    {
                        // Composed eagerly so the factory's own Build-time validation fires at registration
                        // time rather than at first resolve. Without a callback the builder's defaults are built.
                        var cohesionComponentFactory = new global::Test.EndToEndBuilderContributor.ThingBuilder();
                        @configure?.Invoke(cohesionComponentFactory);
                        var cohesionComponent = cohesionComponentFactory.Build();

                        // Registered through the Func<> sink so the container captures the product for
                        // disposal (an instance registration would become an uncaptured ConstantCallSite).
                        builder.AddSingleton<global::Test.EndToEndBuilderContributor.IThing>(_ => cohesionComponent);
                        return builder;
                    }
                """),
                spaces: 8),
            Case.Sensitive);
        AssertNoErrors(result.Compilation);
    }

    private static string ContributorSource(
        string factoryMembers,
        bool includeContract = true,
        string contributorNamespace = "Test.Contributor",
        string verb = "AddProjected",
        string additionalTypes = "")
    {
        string contract = includeContract
            ? $", Contract = typeof({contributorNamespace}.IThing)"
            : string.Empty;

        return $$"""
            #nullable enable
            using System;
            using Assimalign.Cohesion;

            [assembly: ComponentIntegration(
                target: ComponentIntegrationTarget.ServiceProviderBuilder,
                targetMethod: ComponentIntegrationMethod.AddSingleton,
                factoryType: typeof({{contributorNamespace}}.Components),
                factoryMethodName: nameof({{contributorNamespace}}.Components.CreateThing),
                template: ComponentIntegrationTemplate.Factory,
                Verb = "{{verb}}"{{contract}})]

            namespace {{contributorNamespace}};

            public interface IThing
            {
            }

            public sealed class Thing : IThing
            {
            }

            {{additionalTypes}}

            public static class Components
            {
                {{factoryMembers}}
            }
            """;
    }

    private static string BuilderContributorSource(
        string contributorNamespace = "Test.BuilderContributor",
        string verb = "AddProjected",
        string builderMembers = """
            public IThing Build()
                => new Thing();
            """,
        string additionalTypes = "",
        bool includeContract = true) =>
        $$"""
            #nullable enable
            using System;
            using Assimalign.Cohesion;

            [assembly: ComponentIntegration(
                target: ComponentIntegrationTarget.ServiceProviderBuilder,
                targetMethod: ComponentIntegrationMethod.AddSingleton,
                factoryType: typeof({{contributorNamespace}}.ThingBuilder),
                factoryMethodName: nameof({{contributorNamespace}}.ThingBuilder.Build),
                template: ComponentIntegrationTemplate.Builder,
                Verb = "{{verb}}"{{(includeContract ? $", Contract = typeof({contributorNamespace}.IThing)" : string.Empty)}})]

            [assembly: ComponentIntegration(
                target: ComponentIntegrationTarget.ServiceProviderBuilder,
                targetMethod: ComponentIntegrationMethod.AddSingleton,
                factoryType: typeof({{contributorNamespace}}.ThingBuilder),
                factoryMethodName: nameof({{contributorNamespace}}.ThingBuilder.Build),
                template: ComponentIntegrationTemplate.BuilderWithServiceProvider,
                Verb = "{{verb}}"{{(includeContract ? $", Contract = typeof({contributorNamespace}.IThing)" : string.Empty)}})]

            namespace {{contributorNamespace}};

            public interface IThing
            {
            }

            public sealed class Thing : IThing
            {
            }

            {{additionalTypes}}

            public sealed class ThingBuilder
            {
                {{builderMembers}}
            }
            """;

    /// <summary>
    /// Compiles a builder-template contributor whose product records the builder's <c>Name</c>, runs the
    /// generator over a consumer whose <c>Run</c> method executes <paramref name="registrations"/> against a
    /// recording seam, then loads and invokes the consumer and returns each registered product's name in
    /// registration order.
    /// </summary>
    private static string[] ExecuteNamedThingConsumer(string registrations, bool useCustomTemplate = false)
    {
        const string contributorNamespace = "Test.NamedBuilderContributor";
        string source = BuilderContributorSource(
                contributorNamespace: contributorNamespace,
                builderMembers: """
                    public string Name { get; set; } = "default";

                    public IThing Build()
                        => new NamedThing(Name);
                    """,
                additionalTypes: """
                    public sealed class NamedThing : IThing
                    {
                        public NamedThing(string name)
                        {
                            Name = name;
                        }

                        public string Name { get; }
                    }
                    """);
        if (useCustomTemplate)
        {
            source = source.Replace("template: ComponentIntegrationTemplate.Builder,", """"
                template: ComponentIntegrationTemplate.Custom,
                Signature = "global::System.Action<global::Test.NamedBuilderContributor.ThingBuilder>? configure = null",
                Body = """
                {
                    var builder = new $factoryType$();
                    ($arguments$)?.Invoke(builder);
                    var component = builder.$factoryMethod$();
                    $seamType$ integrationTarget = $receiver$;
                    integrationTarget.$targetMethod$<$contract$>(_ => component);
                    return integrationTarget;
                }
                """,
                """");
        }
        byte[] contributorImage = CompileImage(contributorNamespace, source);
        byte[] seamImage = CompileImage("Test.Seam", RecordingSeamSource, includeCore: false);

        GeneratorResult result = Run(
            $$"""
            using System.Linq;
            using Test.NamedBuilderContributor;
            using Assimalign.Cohesion.DependencyInjection;

            public static class Consumer
            {
                public static string[] Run()
                {
                    var seam = new RecordingSeam();
                    {{registrations}}
                    return seam.Products.Select(product => ((NamedThing)product).Name).ToArray();
                }
            }
            """,
            new[] { MetadataReference.CreateFromImage(contributorImage) },
            new[] { MetadataReference.CreateFromImage(seamImage) });
        AssertNoErrors(result.Compilation);

        using var consumerStream = new MemoryStream();
        var emitResult = result.Compilation.Emit(consumerStream);
        emitResult.Success.ShouldBeTrue(FormatDiagnostics(emitResult.Diagnostics));

        var context = new AssemblyLoadContext("ComponentIntegrationGeneratorTests", isCollectible: true);
        try
        {
            var loaded = new Dictionary<string, Assembly>(StringComparer.Ordinal)
            {
                ["Test.Seam"] = context.LoadFromStream(new MemoryStream(seamImage)),
                [contributorNamespace] = context.LoadFromStream(new MemoryStream(contributorImage)),
            };
            context.Resolving += (_, name) =>
                name.Name is not null && loaded.TryGetValue(name.Name, out Assembly? assembly) ? assembly : null;

            consumerStream.Position = 0;
            Assembly consumer = context.LoadFromStream(consumerStream);
            object? registered = consumer.GetType("Consumer", throwOnError: true)!
                .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, null);

            return registered.ShouldBeOfType<string[]>();
        }
        finally
        {
            context.Unload();
        }
    }

    private static MetadataReference CompileReference(
        string assemblyName,
        string source,
        bool includeCore = true) =>
        MetadataReference.CreateFromImage(CompileImage(assemblyName, source, includeCore));

    private static byte[] CompileImage(
        string assemblyName,
        string source,
        bool includeCore = true)
    {
        var references = _platformReferences.ToList();
        if (includeCore)
        {
            references.Add(CoreReference());
        }

        CSharpParseOptions parseOptions = new(LanguageVersion.Preview);
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            ContributorSyntaxTrees(source, parseOptions),
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        using var stream = new MemoryStream();
        var emitResult = compilation.Emit(stream);
        emitResult.Success.ShouldBeTrue(FormatDiagnostics(emitResult.Diagnostics));
        return stream.ToArray();
    }

    private static GeneratorResult Run(
        string consumerSource,
        IEnumerable<MetadataReference> contributors,
        IEnumerable<MetadataReference>? seams = null,
        bool includeCore = true,
        LanguageVersion languageVersion = LanguageVersion.Preview)
    {
        var references = _platformReferences.ToList();
        if (includeCore)
        {
            references.Add(CoreReference());
        }
        references.AddRange(contributors);
        if (seams is not null)
        {
            references.AddRange(seams);
        }

        CSharpParseOptions parseOptions = new(languageVersion);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Test.Consumer",
            new[] { CSharpSyntaxTree.ParseText(consumerSource, parseOptions) },
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new ComponentIntegrationGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation updatedCompilation,
            out _);

        return new GeneratorResult(
            (CSharpCompilation)updatedCompilation,
            driver.GetRunResult());
    }

    private static MetadataReference CoreReference() =>
        MetadataReference.CreateFromFile(typeof(AppEnvironment).Assembly.Location);

    private static IEnumerable<SyntaxTree> ContributorSyntaxTrees(string source, CSharpParseOptions parseOptions)
    {
        yield return CSharpSyntaxTree.ParseText(source, parseOptions);
        if (!source.Contains("ComponentIntegration(") || source.Contains("class ComponentIntegrationAttribute"))
        {
            yield break;
        }

        Assembly assembly = typeof(ComponentIntegrationGeneratorTests).Assembly;
        foreach (string name in assembly.GetManifestResourceNames().Where(static name => name.StartsWith("ComponentIntegration.", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            yield return CSharpSyntaxTree.ParseText(reader.ReadToEnd(), parseOptions, path: name);
        }
    }

    private static string GeneratedText(GeneratorResult result) =>
        string.Join(
            Environment.NewLine,
            result.RunResult.GeneratedTrees.Select(tree => tree.ToString()));

    private static string NormalizeLineEndings(string source) =>
        source.Replace("\r\n", "\n");

    private static string Indent(string source, int spaces)
    {
        string indentation = new(' ', spaces);
        return string.Join(
            "\n",
            source.Split('\n').Select(line => line.Length == 0 ? string.Empty : indentation + line));
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static void AssertNoErrors(CSharpCompilation compilation)
    {
        Diagnostic[] errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        errors.ShouldBeEmpty(FormatDiagnostics(errors));
    }

    private static string FormatDiagnostics(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(diagnostic => diagnostic.ToString()));

    private readonly record struct GeneratorResult(
        CSharpCompilation Compilation,
        GeneratorDriverRunResult RunResult);
}
