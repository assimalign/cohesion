using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

/// <summary>
/// Parses the key-value command grammar (the contract in <c>docs/COMMANDS.md</c>)
/// into typed requests: <c>GET @k</c>, <c>PUT @k @v [IF ABSENT | IF @etag]</c>,
/// <c>DELETE @k [IF @etag]</c>, <c>EXISTS @k</c>, and
/// <c>SCAN [FROM @start] [TO @end] [PREFIX @p] [LIMIT n|@n]</c>. Keywords are
/// case-insensitive; every data operand is a named parameter (<c>@name</c>)
/// riding the session's parameter map — the grammar itself never carries values,
/// which is what keeps it trivially injection-proof and wire-compatible with the
/// protocol's Execute message.
/// </summary>
/// <remarks>
/// Grammar violations, missing parameters, and operand-type mismatches all throw
/// the root's <see cref="DatabaseParseException"/> — they are fix-the-command
/// failures (<c>ParseFailure</c> on the wire), not execution failures.
/// </remarks>
internal static class KeyValueCommandParser
{
    /// <summary>
    /// Parses command text and its bound parameters into a typed request.
    /// </summary>
    /// <param name="statement">The command text.</param>
    /// <param name="parameters">Parameter values keyed by bare name.</param>
    /// <returns>The typed request.</returns>
    /// <exception cref="DatabaseParseException">The text violates the grammar, references a missing parameter, or binds an operand of the wrong type.</exception>
    internal static KeyValueRequest Parse(string statement, IReadOnlyDictionary<string, object?>? parameters)
    {
        string[] tokens = statement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            throw new DatabaseParseException("The command is empty.");
        }

        try
        {
            return tokens[0].ToUpperInvariant() switch
            {
                "GET" => ParseGet(tokens, parameters),
                "PUT" => ParsePut(tokens, parameters),
                "DELETE" => ParseDelete(tokens, parameters),
                "EXISTS" => ParseExists(tokens, parameters),
                "SCAN" => ParseScan(tokens, parameters),
                var verb => throw new DatabaseParseException(
                    $"Unknown command '{verb}'. The key-value grammar is GET, PUT, DELETE, EXISTS, and SCAN (docs/COMMANDS.md)."),
            };
        }
        catch (ArgumentException exception)
        {
            // Request-constructor validation (empty key, contradictory
            // conditions) is a command error at this boundary.
            throw new DatabaseParseException(exception.Message);
        }
    }

    private static KeyValueGetRequest ParseGet(string[] tokens, IReadOnlyDictionary<string, object?>? parameters)
    {
        RequireTokenCount(tokens, 2, "GET @key");
        return new KeyValueGetRequest(BindBytes(tokens[1], parameters));
    }

    private static KeyValueExistsRequest ParseExists(string[] tokens, IReadOnlyDictionary<string, object?>? parameters)
    {
        RequireTokenCount(tokens, 2, "EXISTS @key");
        return new KeyValueExistsRequest(BindBytes(tokens[1], parameters));
    }

    private static KeyValuePutRequest ParsePut(string[] tokens, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (tokens.Length is not (3 or 5))
        {
            throw new DatabaseParseException("PUT takes the form: PUT @key @value [IF ABSENT | IF @etag].");
        }

        var key = BindBytes(tokens[1], parameters);
        var value = BindBytes(tokens[2], parameters);
        KeyValuePutOptions? options = null;

        if (tokens.Length == 5)
        {
            if (!IsKeyword(tokens[3], "IF"))
            {
                throw new DatabaseParseException($"Expected IF but found '{tokens[3]}'. PUT takes the form: PUT @key @value [IF ABSENT | IF @etag].");
            }

            options = IsKeyword(tokens[4], "ABSENT")
                ? new KeyValuePutOptions { OnlyIfAbsent = true }
                : new KeyValuePutOptions { ExpectedETag = BindETag(tokens[4], parameters) };
        }

        return new KeyValuePutRequest(key, value, options);
    }

    private static KeyValueDeleteRequest ParseDelete(string[] tokens, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (tokens.Length is not (2 or 4))
        {
            throw new DatabaseParseException("DELETE takes the form: DELETE @key [IF @etag].");
        }

        var key = BindBytes(tokens[1], parameters);
        long? expectedETag = null;

        if (tokens.Length == 4)
        {
            if (!IsKeyword(tokens[2], "IF"))
            {
                throw new DatabaseParseException($"Expected IF but found '{tokens[2]}'. DELETE takes the form: DELETE @key [IF @etag].");
            }

            expectedETag = BindETag(tokens[3], parameters);
        }

        return new KeyValueDeleteRequest(key, expectedETag);
    }

    private static KeyValueScanRequest ParseScan(string[] tokens, IReadOnlyDictionary<string, object?>? parameters)
    {
        var options = new KeyValueScanOptions();
        int index = 1;

        while (index < tokens.Length)
        {
            string clause = tokens[index].ToUpperInvariant();

            if (index + 1 >= tokens.Length)
            {
                throw new DatabaseParseException($"The {clause} clause is missing its operand. SCAN takes the form: SCAN [FROM @start] [TO @end] [PREFIX @prefix] [LIMIT n].");
            }

            string operand = tokens[index + 1];

            switch (clause)
            {
                case "FROM" when options.Start is null:
                    options.Start = BindBytes(operand, parameters);
                    break;

                case "TO" when options.End is null:
                    options.End = BindBytes(operand, parameters);
                    break;

                case "PREFIX" when options.Prefix is null:
                    options.Prefix = BindBytes(operand, parameters);
                    break;

                case "LIMIT" when options.Limit is null:
                    options.Limit = BindLimit(operand, parameters);
                    break;

                case "FROM" or "TO" or "PREFIX" or "LIMIT":
                    throw new DatabaseParseException($"The {clause} clause appears more than once.");

                default:
                    throw new DatabaseParseException($"Unknown SCAN clause '{tokens[index]}'. SCAN takes the form: SCAN [FROM @start] [TO @end] [PREFIX @prefix] [LIMIT n].");
            }

            index += 2;
        }

        return new KeyValueScanRequest(options);
    }

    // ── Operand binding ────────────────────────────────────────────────

    private static ReadOnlyMemory<byte> BindBytes(string token, IReadOnlyDictionary<string, object?>? parameters)
    {
        object? value = BindParameter(token, parameters);

        return value switch
        {
            byte[] bytes => bytes,
            _ => throw new DatabaseParseException(
                $"Parameter '{token}' must bind a byte[] operand, but carries {value?.GetType().Name ?? "null"}."),
        };
    }

    private static long BindETag(string token, IReadOnlyDictionary<string, object?>? parameters)
    {
        object? value = BindParameter(token, parameters);

        return value switch
        {
            long etag => etag,
            int etag => etag,
            _ => throw new DatabaseParseException(
                $"Parameter '{token}' must bind an integer etag, but carries {value?.GetType().Name ?? "null"}."),
        };
    }

    private static int BindLimit(string token, IReadOnlyDictionary<string, object?>? parameters)
    {
        // LIMIT accepts a non-negative integer literal or a parameter.
        if (token.Length > 0 && token[0] != '@')
        {
            if (!int.TryParse(token, out int literal) || literal < 0)
            {
                throw new DatabaseParseException($"LIMIT requires a non-negative integer, but found '{token}'.");
            }

            return literal;
        }

        object? value = BindParameter(token, parameters);

        return value switch
        {
            int limit when limit >= 0 => limit,
            long limit when limit is >= 0 and <= int.MaxValue => (int)limit,
            _ => throw new DatabaseParseException(
                $"Parameter '{token}' must bind a non-negative integer limit, but carries {value?.GetType().Name ?? "null"}."),
        };
    }

    private static object? BindParameter(string token, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (token.Length < 2 || token[0] != '@')
        {
            throw new DatabaseParseException($"Expected a parameter reference (@name) but found '{token}'.");
        }

        string name = token[1..];

        if (parameters is null || !parameters.TryGetValue(name, out object? value))
        {
            throw new DatabaseParseException($"The command references parameter '@{name}' but no value was bound for it.");
        }

        return value;
    }

    private static bool IsKeyword(string token, string keyword)
        => string.Equals(token, keyword, StringComparison.OrdinalIgnoreCase);

    private static void RequireTokenCount(string[] tokens, int count, string form)
    {
        if (tokens.Length != count)
        {
            throw new DatabaseParseException($"{tokens[0].ToUpperInvariant()} takes the form: {form}.");
        }
    }
}
