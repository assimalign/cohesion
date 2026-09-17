using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Schema.Tests;

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

        SqlSchemaValidationException exception = Should.Throw<SqlSchemaValidationException>(() =>
            CreateSchema([table]));

        exception.Errors.ShouldContain(error =>
            error.Code == SqlSchemaValidationErrorCode.DuplicateDeclaration &&
            error.Declaration == "tables.orders.columns.id");
        exception.Errors.ShouldContain(error =>
            error.Code == SqlSchemaValidationErrorCode.UnknownReference &&
            error.Declaration == "tables.orders.primaryKey.columns.Missing");
        exception.Errors.ShouldContain(error =>
            error.Code == SqlSchemaValidationErrorCode.UnknownReference &&
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

        SqlSchemaValidationException exception = Should.Throw<SqlSchemaValidationException>(() =>
            CreateSchema([lines, orders]));

        SqlSchemaValidationError error = exception.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(SqlSchemaValidationErrorCode.UnsupportedType);
        error.Declaration.ShouldBe(
            "tables.order_lines.constraints.FK_order_lines_orders_OrderId.columns.OrderId");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Compiled schema JSON: unknown members are rejected as typed validation errors")]
    public void Deserialize_WithUnknownMember_ShouldReportJsonPath()
    {
        string document = SqlCompiledSchemaSerializer.Serialize(CreateSchema([CreateOrdersTable()]));
        string malformed = document.Insert(document.Length - 1, ",\"unexpected\":true");

        SqlSchemaValidationException exception = Should.Throw<SqlSchemaValidationException>(() =>
            SqlCompiledSchemaSerializer.Deserialize(malformed));

        SqlSchemaValidationError error = exception.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(SqlSchemaValidationErrorCode.InvalidDocument);
        error.Declaration.ShouldBe("schema.unexpected");
        exception.InnerException.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Compiled schema JSON: malformed input is wrapped as typed validation")]
    public void Deserialize_WithMalformedJson_ShouldReturnTypedValidationError()
    {
        SqlSchemaValidationException exception = Should.Throw<SqlSchemaValidationException>(() =>
            SqlCompiledSchemaSerializer.Deserialize("{ \"format\": "));

        SqlSchemaValidationError error = exception.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(SqlSchemaValidationErrorCode.InvalidDocument);
        error.Declaration.ShouldStartWith("schema");
        exception.InnerException.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Compiled schema JSON: invalid nested state is rejected after deserialization")]
    public void Deserialize_WithNullCollection_ShouldReportCollectionPath()
    {
        string document = SqlCompiledSchemaSerializer.Serialize(CreateSchema([CreateOrdersTable()]));
        string malformed = document.Replace("\"extensions\":[]", "\"extensions\":null", StringComparison.Ordinal);
        malformed.ShouldNotBe(document);

        SqlSchemaValidationException exception = Should.Throw<SqlSchemaValidationException>(() =>
            SqlCompiledSchemaSerializer.Deserialize(malformed));

        exception.Errors.ShouldHaveSingleItem().Declaration.ShouldBe("schema.extensions");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Compiled schema validation: invalid engine models are rejected")]
    public void Create_WithInvalidEngineModel_ShouldReportModelPath()
    {
        SqlSchemaValidationException exception = Should.Throw<SqlSchemaValidationException>(() =>
            new SqlCompiledSchema(
                SqlCompiledSchema.CurrentFormat,
                "orders",
                (EngineModel)byte.MaxValue,
                false,
                [],
                [],
                [],
                [],
                [],
                []));

        SqlSchemaValidationError error = exception.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(SqlSchemaValidationErrorCode.ModelMismatch);
        error.Declaration.ShouldBe("schema.model");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Compiled schema canonicalization: semantic set ordering is stable")]
    public void Create_WithReorderedSemanticSets_ShouldKeepCanonicalDocumentStable()
    {
        SqlCompiledSchema first = CreateOrderedSchema(reverse: false);
        SqlCompiledSchema second = CreateOrderedSchema(reverse: true);

        string document = SqlCompiledSchemaSerializer.Serialize(first);
        SqlCompiledSchemaSerializer.Serialize(second).ShouldBe(document);
        second.Hash.ShouldBe(first.Hash);
        first.Types.Select(type => type.Name).ShouldBe(["AType", "ZType"]);
        first.Tables.Select(table => table.Name).ShouldBe(["alpha", "beta"]);
        first.Tables[0].Indexes.Select(index => index.Name).ShouldBe(["IX_alpha_a", "IX_alpha_z"]);
        first.Tables[0].Constraints.Select(constraint => constraint.Name).ShouldBe(["CK_alpha_a", "CK_alpha_z"]);
        first.Principals[0].Grants.Select(grant => grant.Permission).ShouldBe([SqlPermission.Read, SqlPermission.Write]);
        first.Principals[0].Grants[0].Objects.ShouldBe(["alpha", "beta"]);
        SqlCompiledSchemaSerializer.Serialize(SqlCompiledSchemaSerializer.Deserialize(document)).ShouldBe(document);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Compiled schema validation: a permission is granted once per principal")]
    public void Create_WithRepeatedPermissionGrant_ShouldReportGrantPath()
    {
        CompiledSchemaTable table = CreateOrdersTable();
        var principal = new CompiledSchemaPrincipal(
            "reader",
            [
                new CompiledSchemaGrant(SqlPermission.Read, ["orders"]),
                new CompiledSchemaGrant(SqlPermission.Read, ["orders"]),
            ]);

        SqlSchemaValidationException exception = Should.Throw<SqlSchemaValidationException>(() =>
            new SqlCompiledSchema(
                SqlCompiledSchema.CurrentFormat,
                "orders",
                EngineModel.Sql,
                false,
                [],
                [table],
                [],
                [],
                [principal],
                []));

        SqlSchemaValidationError error = exception.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(SqlSchemaValidationErrorCode.DuplicateDeclaration);
        error.Declaration.ShouldBe("principals.reader.grants[1]");
    }

    private static SqlCompiledSchema CreateSchema(IReadOnlyList<CompiledSchemaTable> tables)
        => new(
            SqlCompiledSchema.CurrentFormat,
            "orders",
            EngineModel.Sql,
            false,
            [],
            tables,
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

    private static SqlCompiledSchema CreateOrderedSchema(bool reverse)
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
            SqlPermission.Read,
            reverse ? ["beta", "alpha"] : ["alpha", "beta"]);
        var write = new CompiledSchemaGrant(
            SqlPermission.Write,
            reverse ? ["beta", "alpha"] : ["alpha", "beta"]);

        return new SqlCompiledSchema(
            SqlCompiledSchema.CurrentFormat,
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
            [new CompiledSchemaPrincipal("reader", reverse ? [write, read] : [read, write])],
            reverse
                ? [new CompiledSchemaExtension("z", "2"), new CompiledSchemaExtension("a", "1")]
                : [new CompiledSchemaExtension("a", "1"), new CompiledSchemaExtension("z", "2")]);
    }
}
