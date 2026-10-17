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
        if (exception is SqlClientException || exception is not DatabaseException failure || expected.Length < 10 || !expected.StartsWith("COHSQLE", StringComparison.Ordinal))
        {
            return expected;
        }
        failure.ShouldHaveDiagnostic(expected[..10]);
        for (int start = expected.IndexOf("COHSQLE", StringComparison.Ordinal); start >= 0;
            start = expected.IndexOf("COHSQLE", StringComparison.Ordinal))
        {
            int length = 10;
            if (start + length < expected.Length && expected[start + length] == ':') { length++; }
            if (start + length < expected.Length && expected[start + length] == ' ') { length++; }
            expected = expected.Remove(start, length);
        }
        return expected;
    }

    extension(DatabaseException exception)
    {
        internal string DiagnosticDetail(string code)
        {
            exception.ShouldHaveDiagnostic(code);
            return exception is SqlClientException ? exception.Message[(code.Length + 2)..] : exception.Message;
        }

        internal DatabaseException ShouldHaveDiagnostic(string code)
        {
            if (exception is SqlClientException)
            {
                exception.Message.ShouldStartWith(code + ": ", Case.Sensitive);
                return exception;
            }
            exception.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code && diagnostic.Severity == DiagnosticSeverity.Error);
            exception.Message.ShouldNotContain(code + ":", Case.Sensitive);
            return exception;
        }
    }
}
