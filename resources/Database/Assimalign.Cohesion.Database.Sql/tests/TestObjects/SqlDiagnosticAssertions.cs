using System;

using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

using Shouldly;

namespace Assimalign.Cohesion.Database.Sql.Tests;

internal static class SqlDiagnosticAssertions
{
    extension(ProtocolErrorMessage error)
    {
        internal void ShouldHaveMessage(string expected, string? customMessage = null)
            => error.Message.ShouldBe(expected, customMessage);

        internal void ShouldStartWithMessage(string expected, Case caseSensitivity = Case.Insensitive, string? customMessage = null)
            => error.Message.ShouldStartWith(expected, caseSensitivity, customMessage);

        internal void ShouldContainMessage(string expected, Case caseSensitivity = Case.Insensitive, string? customMessage = null)
            => error.Message.ShouldContain(expected, caseSensitivity, customMessage);
    }

    extension(Exception exception)
    {
        internal void ShouldHaveMessage(string expected, string? customMessage = null)
            => exception.Message.ShouldBe(DiagnosticMessage(exception, expected), customMessage);

        internal void ShouldStartWithMessage(string expected, Case caseSensitivity = Case.Insensitive, string? customMessage = null)
            => exception.Message.ShouldStartWith(DiagnosticMessage(exception, expected), caseSensitivity, customMessage);

        internal void ShouldContainMessage(string expected, Case caseSensitivity = Case.Insensitive, string? customMessage = null)
            => exception.Message.ShouldContain(DiagnosticMessage(exception, expected), caseSensitivity, customMessage);
    }

    // Old error fixtures contain CODE: detail. In process, assert that code on the carrier and
    // compare the unprefixed detail. A wire exception retains its exact 1.0 text assertion.
    private static string DiagnosticMessage(Exception exception, string expected)
    {
        if (exception is SqlClientException || exception is not DatabaseException failure
            || expected.Length < 7 || !IsExecutionCode(expected.AsSpan(0, 7)))
        {
            return expected;
        }
        failure.ShouldHaveDiagnostic(expected[..7]);
        for (int start = 0; start + 7 <= expected.Length;)
        {
            if (!IsExecutionCode(expected.AsSpan(start, 7))) { start++; continue; }
            int length = 7;
            if (start + length < expected.Length && expected[start + length] == ':') { length++; }
            if (start + length < expected.Length && expected[start + length] == ' ') { length++; }
            expected = expected.Remove(start, length);
        }
        return expected;
    }

    // Preserve the former COHSQLE fixture normalization exactly; other legacy message-only
    // families retain their own assertions. These identities now span several categories.
    private static bool IsExecutionCode(ReadOnlySpan<char> code) => code is
        "CS01005" or "CS01006" or "CS01008" or "CS01009" or "CS01014" or
        "CS01015" or "CS01016" or "CS01019" or "CS01020" or "CS01023" or
        "CS01026" or "CS01028" or "CS01051" or "CS01052" or "CS02001" or
        "CS02002" or "CS02003" or "CS02010" or "CS02011" or "CS02012" or
        "CS02018" or "CS02022" or "CS02034" or "CS02046" or "CS02047" or
        "CS02048" or "CS02055" or "CS02062" or "CS02063" or "CS03024" or
        "CS03025" or "CS03027" or "CS03029" or "CS03030" or "CS03031" or
        "CS03033" or "CS03035" or "CS03037" or "CS03038" or "CS03039" or
        "CS03049" or "CS03050" or "CS03053" or "CS03054" or "CS03056" or
        "CS03059" or "CS03061" or "CS03065" or "CS03066" or "CS04021" or
        "CS04044" or "CS04064" or "CS05004" or "CS05007" or "CS05013" or
        "CS05017" or "CS05032" or "CS05036" or "CS05040" or "CS05042" or
        "CS05043" or "CS05045" or "CS05057" or "CS05060" or "CS05067" or
        "CS06041" or "CS06058";

    extension(DatabaseException exception)
    {
        internal string DiagnosticDetail(string code)
        {
            exception.ShouldHaveDiagnostic(code);
            return exception.Message;
        }

        internal DatabaseException ShouldHaveDiagnostic(string code)
        {
            exception.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code && diagnostic.Severity == DiagnosticSeverity.Error);
            exception.Message.ShouldNotContain(code + ":", Case.Sensitive);
            return exception;
        }
    }
}
