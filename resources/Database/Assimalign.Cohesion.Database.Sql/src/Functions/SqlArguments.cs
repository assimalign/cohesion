using System;
using System.Threading;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// The arguments of one call of a <see cref="SqlFunction"/>, converted to the types the function
/// declares, and the call's <see cref="Context"/>.
/// </summary>
/// <remarks>
/// <para>
/// A ref struct over values the engine holds on the stack (four inline, a pooled buffer past four),
/// so a call allocates nothing and a function cannot keep its arguments beyond the call. Each
/// <c>Get…</c> accessor reads one argument with the matching <see cref="SqlValue"/> accessor and
/// throws <see cref="InvalidCastException"/> for <c>NULL</c> or another type. A strict function
/// (<see cref="SqlNullBehavior.ReturnsNullOnNullInput"/>) never sees a <c>NULL</c> argument; a
/// function over a pseudo-type reads <see cref="SqlValue.Type"/> first.
/// </para>
/// </remarks>
public readonly ref struct SqlArguments
{
    private readonly ReadOnlySpan<SqlValue> _values;
    private readonly SqlFunctionContext _context;

    /// <summary>
    /// Initializes arguments with the default context, for calling a function directly, as a test
    /// of the function does.
    /// </summary>
    /// <param name="values">The argument values, in parameter order.</param>
    public SqlArguments(ReadOnlySpan<SqlValue> values)
    {
        _values = values;
        _context = default;
    }

    /// <summary>
    /// Initializes arguments with a context: the engine's call, or a test of a function that reads
    /// its context's collation, database or cancellation token.
    /// </summary>
    /// <param name="values">The argument values, in parameter order.</param>
    /// <param name="context">The call's context.</param>
    public SqlArguments(ReadOnlySpan<SqlValue> values, scoped in SqlFunctionContext context)
    {
        _values = values;
        _context = context;
    }

    /// <summary>
    /// Initializes the engine's arguments of one call, the context built in place from its parts,
    /// so the caller's frame holds no context of its own to clear and copy.
    /// </summary>
    /// <param name="values">The argument values, in parameter order.</param>
    /// <param name="database">The database whose statement makes the call.</param>
    /// <param name="collation">The collation the call's input compares under.</param>
    /// <param name="cancellationToken">The statement's cancellation token.</param>
    internal SqlArguments(ReadOnlySpan<SqlValue> values, DatabaseName database, Collation collation, CancellationToken cancellationToken)
    {
        _values = values;
        _context = new SqlFunctionContext(database, collation, cancellationToken);
    }

    /// <summary>Gets the number of arguments.</summary>
    public int Count => _values.Length;

    /// <summary>Gets an argument.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside the arguments.</exception>
    public SqlValue this[int index] => _values[index];

    /// <summary>Gets the call's context.</summary>
    public SqlFunctionContext Context => _context;

    /// <summary>Whether an argument is SQL <c>NULL</c>.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns><see langword="true"/> for <c>NULL</c>.</returns>
    public bool IsNull(int index) => _values[index].IsNull;

    /// <summary>Reads a <c>BOOLEAN</c> argument.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The argument is NULL or of another type.</exception>
    public bool GetBoolean(int index) => _values[index].AsBoolean();

    /// <summary>Reads a <c>TINYINT</c> argument.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The argument is NULL or of another type.</exception>
    public sbyte GetSByte(int index) => _values[index].AsSByte();

    /// <summary>Reads a <c>SMALLINT</c> argument.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The argument is NULL or of another type.</exception>
    public short GetInt16(int index) => _values[index].AsInt16();

    /// <summary>Reads an <c>INTEGER</c> argument.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The argument is NULL or of another type.</exception>
    public int GetInt32(int index) => _values[index].AsInt32();

    /// <summary>Reads a <c>BIGINT</c> argument.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The argument is NULL or of another type.</exception>
    public long GetInt64(int index) => _values[index].AsInt64();

    /// <summary>Reads a <c>REAL</c> argument.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The argument is NULL or of another type.</exception>
    public float GetSingle(int index) => _values[index].AsSingle();

    /// <summary>Reads a <c>DOUBLE</c> argument.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The argument is NULL or of another type.</exception>
    public double GetDouble(int index) => _values[index].AsDouble();

    /// <summary>Reads a <c>NUMERIC</c> argument.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The argument is NULL or of another type.</exception>
    public decimal GetDecimal(int index) => _values[index].AsDecimal();

    /// <summary>Reads a <c>TEXT</c> argument.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The argument is NULL or of another type.</exception>
    public string GetString(int index) => _values[index].AsString();

    /// <summary>Reads a binary argument, read-only: the bytes are the engine's (see <see cref="SqlValue.AsBinary"/>).</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The argument is NULL or of another type.</exception>
    public ReadOnlyMemory<byte> GetBinary(int index) => _values[index].AsBinary();

    /// <summary>Reads a <c>DATE</c> argument.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The argument is NULL or of another type.</exception>
    public DateOnly GetDate(int index) => _values[index].AsDate();

    /// <summary>Reads a <c>TIME</c> argument.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The argument is NULL or of another type.</exception>
    public TimeOnly GetTime(int index) => _values[index].AsTime();

    /// <summary>Reads a <c>TIMESTAMP</c> argument.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The argument is NULL or of another type.</exception>
    public DateTime GetDateTime(int index) => _values[index].AsDateTime();

    /// <summary>Reads a <c>TIMESTAMPTZ</c> argument.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The argument is NULL or of another type.</exception>
    public DateTimeOffset GetDateTimeOffset(int index) => _values[index].AsDateTimeOffset();

    /// <summary>Reads an <c>INTERVAL</c> argument.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The argument is NULL or of another type.</exception>
    public TimeSpan GetTimeSpan(int index) => _values[index].AsTimeSpan();

    /// <summary>Reads a <c>UUID</c> argument.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The argument is NULL or of another type.</exception>
    public Guid GetGuid(int index) => _values[index].AsGuid();

    /// <summary>Reads an argument's storage type in place, without copying the value.</summary>
    /// <param name="index">The argument's position, from zero.</param>
    /// <returns>The type; <see cref="DatabaseType.Null"/> for <c>NULL</c>.</returns>
    internal DatabaseType TypeAt(int index) => _values[index].Type;

    /// <summary>Gets whether any argument is SQL <c>NULL</c>: the strict short-circuit's test.</summary>
    internal bool HasNull
    {
        get
        {
            foreach (ref readonly SqlValue value in _values)
            {
                if (value.IsNull)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
