using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.KeyValuePair.Protocol;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Tests;

public sealed class KeyValueFunctionClientTests
{
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - Functions: typed CALL exchanges preserve primitive types and binary ownership")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeFunctionAsync_InMemoryAndTcp_ShouldRoundTripAsync(bool tcp)
    {
        int calls = 0;
        var observer = new RecordingObserver();
        await using var harness = await KeyValueClientTestHarness.StartAsync(tcp: tcp, observer: observer, configureFunctions: functions =>
        {
            functions.Add(KeyValueFunction.Create<long, long>("increment", value => { Interlocked.Increment(ref calls); return value + 1; }));
            functions.Add(KeyValueFunction.Create<string?, string?>("nullable", value => value, KeyValueNullBehavior.CalledOnNullInput));
            functions.Add(KeyValueFunction.Create<byte[], byte[]>("binary", value => { value[0]++; return value; }));
            functions.Add(KeyValueFunction.Create<string>("zero", () => "ready"));
        });
        await using var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout());
        (await connection.InvokeFunctionAsync("InCrEmEnT", [41L], KeyValueClientTestHarness.Timeout())).ShouldBe(42L);
        calls.ShouldBe(1);
        (await connection.InvokeFunctionAsync("nullable", [null], KeyValueClientTestHarness.Timeout())).ShouldBeNull();
        (await connection.InvokeFunctionAsync("zero", [], KeyValueClientTestHarness.Timeout())).ShouldBe("ready");
        byte[] bytes = [7, 8];
        var result = (await connection.InvokeFunctionAsync("binary", [bytes], KeyValueClientTestHarness.Timeout())).ShouldBeOfType<byte[]>();
        result.ShouldBe(new byte[] { 8, 8 });
        bytes.ShouldBe(new byte[] { 7, 8 });
        observer.Executing.ShouldBe(["CALL InCrEmEnT @p0", "CALL nullable @p0", "CALL zero", "CALL binary @p0"]);
        connection.IsOpen.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - Functions: every native primitive uses the existing value codec")]
    public async Task InvokeFunctionAsync_AllNativePrimitives_ShouldPreserveExactTypesAsync()
    {
        await using var harness = await KeyValueClientTestHarness.StartAsync(configureFunctions: functions =>
        {
            functions.Add(KeyValueFunction.Create<bool, bool>("f0", value => value));
            functions.Add(KeyValueFunction.Create<sbyte, sbyte>("f1", value => value));
            functions.Add(KeyValueFunction.Create<short, short>("f2", value => value));
            functions.Add(KeyValueFunction.Create<int, int>("f3", value => value));
            functions.Add(KeyValueFunction.Create<long, long>("f4", value => value));
            functions.Add(KeyValueFunction.Create<float, float>("f5", value => value));
            functions.Add(KeyValueFunction.Create<double, double>("f6", value => value));
            functions.Add(KeyValueFunction.Create<decimal, decimal>("f7", value => value));
            functions.Add(KeyValueFunction.Create<string, string>("f8", value => value));
            functions.Add(KeyValueFunction.Create<byte[], byte[]>("f9", value => value));
            functions.Add(KeyValueFunction.Create<DateOnly, DateOnly>("f10", value => value));
            functions.Add(KeyValueFunction.Create<TimeOnly, TimeOnly>("f11", value => value));
            functions.Add(KeyValueFunction.Create<DateTime, DateTime>("f12", value => value));
            functions.Add(KeyValueFunction.Create<DateTimeOffset, DateTimeOffset>("f13", value => value));
            functions.Add(KeyValueFunction.Create<TimeSpan, TimeSpan>("f14", value => value));
            functions.Add(KeyValueFunction.Create<Guid, Guid>("f15", value => value));
        });
        await using var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout());
        object?[] values = [true, (sbyte)-3, (short)5, 7, 11L, 1.25f, 2.5d, 3.75m, "hello", new byte[] { 1, 2 },
            new DateOnly(2026, 10, 24), new TimeOnly(12, 34, 56), new DateTime(2026, 10, 24, 12, 34, 56),
            new DateTimeOffset(2026, 10, 24, 12, 34, 56, TimeSpan.FromHours(2)), TimeSpan.FromSeconds(17), Guid.NewGuid()];
        for (int index = 0; index < values.Length; index++)
        {
            object? actual = await connection.InvokeFunctionAsync("f" + index, [values[index]], KeyValueClientTestHarness.Timeout());
            actual.ShouldBe(values[index]);
            actual!.GetType().ShouldBe(values[index]!.GetType());
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - Functions: coded execution errors preserve a healthy reusable connection")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InvokeFunctionAsync_ResolutionAndBodyFault_ShouldKeepConnectionAsync(bool bodyFault, bool tcp)
    {
        await using var harness = await KeyValueClientTestHarness.StartAsync(tcp: tcp, configureFunctions: functions =>
            functions.Add(KeyValueFunction.Create<long>("fault", () => throw new TimeoutException("body-fault"))));
        await using var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout());
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        var error = await Should.ThrowAsync<KeyValueClientException>(async () =>
            await connection.InvokeFunctionAsync(bodyFault ? "fault" : "missing", [], KeyValueClientTestHarness.Timeout()));
        error.Kind.ShouldBe(KeyValueClientErrorKind.ExecutionFailure);
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldStartWith(bodyFault ? "CV05003:" : "CV01018:");
        error.ConnectionUsable.ShouldBeTrue();
        connection.IsOpen.ShouldBeTrue();
        (await connection.ExistsAsync(new byte[] { 42 }, KeyValueClientTestHarness.Timeout())).ShouldBeFalse();
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - Functions: a canceled CALL is not invoked and the pool accepts a later rental")]
    public async Task InvokeFunctionAsync_PreCanceled_ShouldPreserveCancellationAsync()
    {
        // Arrange
        int calls = 0;
        await using var harness = await KeyValueClientTestHarness.StartAsync(configureFunctions: functions =>
            functions.Add(KeyValueFunction.Create("f", () => { Interlocked.Increment(ref calls); return 1L; })));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        await using (var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout()))
        {
            OperationCanceledException? error = null;
            try
            {
                await connection.InvokeFunctionAsync("f", [], cancellation.Token);
            }
            catch (OperationCanceledException failure)
            {
                error = failure;
            }
            error.ShouldNotBeNull();
            error.CancellationToken.ShouldBe(cancellation.Token);
            connection.IsOpen.ShouldBeTrue();
        }

        // Assert: cancellation before the exchange writes nothing and never enters the server body.
        calls.ShouldBe(0);
        await using var next = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout());
        (await next.InvokeFunctionAsync("f", [], KeyValueClientTestHarness.Timeout())).ShouldBe(1L);
        calls.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - Functions: invalid arguments and stale rentals never send CALL")]
    public async Task InvokeFunctionAsync_InvalidArguments_ShouldStayLocalAsync()
    {
        var observer = new RecordingObserver();
        await using var harness = await KeyValueClientTestHarness.StartAsync(observer: observer);
        var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout());
        await Should.ThrowAsync<ArgumentException>(async () => await connection.InvokeFunctionAsync(default(KeyValueFunctionName), [], CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(async () => await connection.InvokeFunctionAsync("echo", [new object()], CancellationToken.None));
        await Should.ThrowAsync<ArgumentNullException>(async () => await connection.InvokeFunctionAsync("echo", null!, CancellationToken.None));
        observer.Executing.ShouldBeEmpty();
        await connection.DisposeAsync();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await connection.InvokeFunctionAsync("echo", [], CancellationToken.None));
    }
}
