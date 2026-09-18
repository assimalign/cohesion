using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Tests;

public sealed class BlobApplicationBuilderTests
{
    [Fact]
    public async Task Registration_uses_only_root_builder_and_grouped_memory_is_rejected()
    {
        // Pre-#1018 this test asserted successful grouped writes on memory because
        // durability silently degraded; explicit Grouped must now fail at open.
        var builder = new RecordingBuilder();
        await using var engine = builder.AddBlobDatabase(options =>
        {
            options.EngineName = "registered";
            options.Durability = StorageCommitDurability.Grouped;
        });
        builder.Engines.ShouldHaveSingleItem().ShouldBeSameAs(engine);
        engine.Name.ShouldBe("registered");
        engine.State.ShouldBe(EngineState.Running);
        engine.Model.ShouldBe(EngineModel.Blob);
        var failure = await Should.ThrowAsync<NotSupportedException>(async () => await engine.CreateDatabaseAsync("test"));
        failure.Message.ShouldContain("BlobStorage (test)");
        failure.Message.ShouldContain(nameof(StorageCommitDurability.Grouped));
    }

    private sealed class RecordingBuilder : IDatabaseApplicationBuilder
    {
        private readonly List<IDatabaseEngine> _engines = [];
        public IReadOnlyList<IDatabaseEngine> Engines => _engines;
        public IDatabaseApplicationBuilder AddEngine(IDatabaseEngine engine) { _engines.Add(engine); return this; }
        public IDatabaseApplicationBuilder AddServer(IDatabaseServer server) => throw new NotSupportedException();
        public IDatabaseApplicationBuilder AddServer(Func<IDatabaseApplicationContext, IDatabaseServer> configure) => throw new NotSupportedException();
        public IDatabaseApplication Build() => throw new NotSupportedException();
    }
}
