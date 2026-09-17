using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphApplicationBuilderTests
{
    [Fact]
    public async Task RootBuilderRegistration_ShouldCreateOperationalGroupedGraphEngine()
    {
        var builder = new RecordingBuilder();
        await using var engine = builder.AddGraphDatabase(options =>
        {
            options.EngineName = "registered";
            options.Durability = StorageCommitDurability.Grouped;
        });
        builder.Engines.ShouldHaveSingleItem().ShouldBeSameAs(engine);
        engine.Name.ShouldBe("registered");
        engine.State.ShouldBe(EngineState.Running);
        engine.Model.ShouldBe(EngineModel.Graph);
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var node = await database.CreateNodeAsync(session, ["Person"], new Dictionary<string, object?> { ["name"] = "Ada" });
        (await database.GetNodeAsync(session, node.Id)).ShouldNotBeNull().Properties["name"].ShouldBe("Ada");
    }

    [Fact]
    public void RejectedRegistration_ShouldDisposeNewEngineAndPreserveRegistrationFailure()
    {
        var builder = new RecordingBuilder(reject: true);
        try
        {
            var error = Should.Throw<InvalidOperationException>(() => builder.AddGraphDatabase());
            error.Message.ShouldBe("Registration refused.");
            builder.Engines.ShouldHaveSingleItem().State.ShouldBe(EngineState.Disposed);
        }
        finally
        {
            foreach (var engine in builder.Engines) { engine.Dispose(); }
        }
    }

    private sealed class RecordingBuilder(bool reject = false) : IDatabaseApplicationBuilder
    {
        private readonly List<IDatabaseEngine> _engines = [];
        public IReadOnlyList<IDatabaseEngine> Engines => _engines;
        public IDatabaseApplicationBuilder AddEngine(IDatabaseEngine engine)
        {
            _engines.Add(engine);
            if (reject) { throw new InvalidOperationException("Registration refused."); }
            return this;
        }
        public IDatabaseApplicationBuilder AddServer(IDatabaseServer server) => throw new NotSupportedException();
        public IDatabaseApplicationBuilder AddServer(Func<IDatabaseApplicationContext, IDatabaseServer> configure) => throw new NotSupportedException();
        public IDatabaseApplication Build() => throw new NotSupportedException();
    }
}
