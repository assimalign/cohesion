using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

public class CompiledSchemaValidationTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Compiled schema validation: malformed AST reports named declaration paths")]
    public void Create_WithMalformedAst_ShouldReportNamedDeclarationPaths()
    {
        var table = new CompiledSchemaTable(
            "orders",
            "Example.Order",
            [
                new CompiledSchemaColumn("Id", DatabaseType.Int64, false),
                new CompiledSchemaColumn("id", DatabaseType.String, false),
            ],
            new CompiledSchemaKey("PK_orders", ["Missing"]),
            [new CompiledSchemaIndex("IX_orders_missing", ["Missing"])],
            []);

        DatabaseSchemaValidationException exception = Should.Throw<DatabaseSchemaValidationException>(() =>
            CreateSchema([table]));

        exception.Errors.ShouldContain(error =>
            error.Code == DatabaseSchemaValidationErrorCode.DuplicateDeclaration &&
            error.Declaration == "tables.orders.columns.id");
        exception.Errors.ShouldContain(error =>
            error.Code == DatabaseSchemaValidationErrorCode.UnknownReference &&
            error.Declaration == "tables.orders.primaryKey.columns.Missing");
        exception.Errors.ShouldContain(error =>
            error.Code == DatabaseSchemaValidationErrorCode.UnknownReference &&
            error.Declaration == "tables.orders.indexes.IX_orders_missing.columns.Missing");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Compiled schema validation: foreign-key column types must match")]
    public void Create_WithMismatchedReferenceTypes_ShouldReportConstraintPath()
    {
        var orders = new CompiledSchemaTable(
            "orders",
            "Example.Order",
            [new CompiledSchemaColumn("Id", DatabaseType.Int64, false)],
            new CompiledSchemaKey("PK_orders", ["Id"]),
            [],
            []);
        var lines = new CompiledSchemaTable(
            "order_lines",
            "Example.OrderLine",
            [
                new CompiledSchemaColumn("Id", DatabaseType.Int64, false),
                new CompiledSchemaColumn("OrderId", DatabaseType.String, false),
            ],
            new CompiledSchemaKey("PK_order_lines", ["Id"]),
            [],
            [new CompiledSchemaConstraint(
                "FK_order_lines_orders_OrderId",
                CompiledSchemaConstraintKind.Reference,
                ["OrderId"],
                "orders",
                ["Id"])]);

        DatabaseSchemaValidationException exception = Should.Throw<DatabaseSchemaValidationException>(() =>
            CreateSchema([lines, orders]));

        DatabaseSchemaValidationError error = exception.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DatabaseSchemaValidationErrorCode.UnsupportedType);
        error.Declaration.ShouldBe(
            "tables.order_lines.constraints.FK_order_lines_orders_OrderId.columns.OrderId");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Compiled schema JSON: unknown members are rejected as typed validation errors")]
    public void Deserialize_WithUnknownMember_ShouldReportJsonPath()
    {
        string document = CompiledSchemaSerializer.Serialize(CreateSchema([CreateOrdersTable()]));
        string malformed = document.Insert(document.Length - 1, ",\"unexpected\":true");

        DatabaseSchemaValidationException exception = Should.Throw<DatabaseSchemaValidationException>(() =>
            CompiledSchemaSerializer.Deserialize(malformed));

        DatabaseSchemaValidationError error = exception.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DatabaseSchemaValidationErrorCode.InvalidDocument);
        error.Declaration.ShouldBe("schema.unexpected");
        exception.InnerException.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Compiled schema JSON: malformed input is wrapped as typed validation")]
    public void Deserialize_WithMalformedJson_ShouldReturnTypedValidationError()
    {
        DatabaseSchemaValidationException exception = Should.Throw<DatabaseSchemaValidationException>(() =>
            CompiledSchemaSerializer.Deserialize("{ \"format\": "));

        DatabaseSchemaValidationError error = exception.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DatabaseSchemaValidationErrorCode.InvalidDocument);
        error.Declaration.ShouldStartWith("schema");
        exception.InnerException.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Compiled schema JSON: invalid nested state is rejected after deserialization")]
    public void Deserialize_WithNullCollection_ShouldReportCollectionPath()
    {
        string document = CompiledSchemaSerializer.Serialize(CreateSchema([CreateOrdersTable()]));
        string malformed = document.Replace("\"extensions\":[]", "\"extensions\":null", StringComparison.Ordinal);
        malformed.ShouldNotBe(document);

        DatabaseSchemaValidationException exception = Should.Throw<DatabaseSchemaValidationException>(() =>
            CompiledSchemaSerializer.Deserialize(malformed));

        exception.Errors.ShouldHaveSingleItem().Declaration.ShouldBe("schema.extensions");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Compiled schema validation: invalid engine models are rejected")]
    public void Create_WithInvalidEngineModel_ShouldReportModelPath()
    {
        DatabaseSchemaValidationException exception = Should.Throw<DatabaseSchemaValidationException>(() =>
            new CompiledSchema(
                CompiledSchema.CurrentFormat,
                "orders",
                (EngineModel)byte.MaxValue,
                false,
                [],
                [],
                [],
                [],
                [],
                [],
                []));

        DatabaseSchemaValidationError error = exception.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DatabaseSchemaValidationErrorCode.ModelMismatch);
        error.Declaration.ShouldBe("schema.model");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Compiled schema canonicalization: semantic set ordering is stable")]
    public void Create_WithReorderedSemanticSets_ShouldKeepCanonicalDocumentStable()
    {
        CompiledSchema first = CreateOrderedSchema(reverse: false);
        CompiledSchema second = CreateOrderedSchema(reverse: true);

        string document = CompiledSchemaSerializer.Serialize(first);
        CompiledSchemaSerializer.Serialize(second).ShouldBe(document);
        second.Hash.ShouldBe(first.Hash);
        first.Types.Select(type => type.Name).ShouldBe(["AType", "ZType"]);
        first.Tables.Select(table => table.Name).ShouldBe(["alpha", "beta"]);
        first.Tables[0].Indexes.Select(index => index.Name).ShouldBe(["IX_alpha_a", "IX_alpha_z"]);
        first.Tables[0].Constraints.Select(constraint => constraint.Name).ShouldBe(["CK_alpha_a", "CK_alpha_z"]);
        first.Principals[0].Grants.Select(grant => grant.Permission).ShouldBe([Permission.Read, Permission.Write]);
        first.Principals[0].Grants[0].Objects.ShouldBe(["alpha", "beta"]);
        CompiledSchemaSerializer.Serialize(CompiledSchemaSerializer.Deserialize(document)).ShouldBe(document);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Compiled schema validation: a permission is granted once per principal")]
    public void Create_WithRepeatedPermissionGrant_ShouldReportGrantPath()
    {
        CompiledSchemaTable table = CreateOrdersTable();
        var principal = new CompiledSchemaPrincipal(
            "reader",
            [
                new CompiledSchemaGrant(Permission.Read, ["orders"]),
                new CompiledSchemaGrant(Permission.Read, ["orders"]),
            ]);

        DatabaseSchemaValidationException exception = Should.Throw<DatabaseSchemaValidationException>(() =>
            new CompiledSchema(
                CompiledSchema.CurrentFormat,
                "orders",
                EngineModel.Sql,
                false,
                [],
                [table],
                [],
                [],
                [],
                [principal],
                []));

        DatabaseSchemaValidationError error = exception.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(DatabaseSchemaValidationErrorCode.DuplicateDeclaration);
        error.Declaration.ShouldBe("principals.reader.grants[1]");
    }

    private static CompiledSchema CreateSchema(IReadOnlyList<CompiledSchemaTable> tables)
        => new(
            CompiledSchema.CurrentFormat,
            "orders",
            EngineModel.Sql,
            false,
            [],
            tables,
            [],
            [],
            [],
            [],
            []);

    private static CompiledSchemaTable CreateOrdersTable()
        => new(
            "orders",
            "Example.Order",
            [new CompiledSchemaColumn("Id", DatabaseType.Int64, false)],
            new CompiledSchemaKey("PK_orders", ["Id"]),
            [],
            []);

    private static CompiledSchema CreateOrderedSchema(bool reverse)
    {
        var checkA = new CompiledSchemaConstraint(
            "CK_alpha_a",
            CompiledSchemaConstraintKind.Check,
            ["Id"],
            null,
            [],
            new CompiledSchemaExpression("p0"));
        var checkZ = new CompiledSchemaConstraint(
            "CK_alpha_z",
            CompiledSchemaConstraintKind.Check,
            ["Id"],
            null,
            [],
            new CompiledSchemaExpression("p0"));
        var alpha = new CompiledSchemaTable(
            "alpha",
            "Example.Alpha",
            [new CompiledSchemaColumn("Id", DatabaseType.Int64, false)],
            new CompiledSchemaKey("PK_alpha", ["Id"]),
            reverse
                ? [new CompiledSchemaIndex("IX_alpha_z", ["Id"]), new CompiledSchemaIndex("IX_alpha_a", ["Id"])]
                : [new CompiledSchemaIndex("IX_alpha_a", ["Id"]), new CompiledSchemaIndex("IX_alpha_z", ["Id"])],
            reverse ? [checkZ, checkA] : [checkA, checkZ]);
        var beta = new CompiledSchemaTable(
            "beta",
            "Example.Beta",
            [new CompiledSchemaColumn("Id", DatabaseType.Int64, false)],
            new CompiledSchemaKey("PK_beta", ["Id"]),
            [],
            []);
        var read = new CompiledSchemaGrant(
            Permission.Read,
            reverse ? ["beta", "alpha"] : ["alpha", "beta"]);
        var write = new CompiledSchemaGrant(
            Permission.Write,
            reverse ? ["beta", "alpha"] : ["alpha", "beta"]);

        return new CompiledSchema(
            CompiledSchema.CurrentFormat,
            "canonical",
            EngineModel.Sql,
            false,
            reverse
                ? [
                    new CompiledSchemaType("ZType", DatabaseType.Decimal, 18, 2),
                    new CompiledSchemaType("AType", DatabaseType.Decimal, 18, 2),
                ]
                : [
                    new CompiledSchemaType("AType", DatabaseType.Decimal, 18, 2),
                    new CompiledSchemaType("ZType", DatabaseType.Decimal, 18, 2),
                ],
            reverse ? [beta, alpha] : [alpha, beta],
            [],
            [],
            [],
            [new CompiledSchemaPrincipal("reader", reverse ? [write, read] : [read, write])],
            reverse
                ? [new CompiledSchemaExtension("z", "2"), new CompiledSchemaExtension("a", "1")]
                : [new CompiledSchemaExtension("a", "1"), new CompiledSchemaExtension("z", "2")]);
    }
}
