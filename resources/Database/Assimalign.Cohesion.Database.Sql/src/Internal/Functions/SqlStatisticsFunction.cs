using System;
using System.Numerics;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>PostgreSQL sample/population statistics, with exact integer and decimal transitions.</summary>
[System.Diagnostics.DebuggerDisplay("SqlStatisticsFunction {Name,nq}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(SqlStatisticsFunction.DebugView))]
internal sealed class SqlStatisticsFunction : SqlAggregateFunction
{
    private readonly bool _sample;
    private readonly bool _squareRoot;

    internal SqlStatisticsFunction(string name, bool sample, bool squareRoot, bool floating = false)
        : base(name, [floating ? SqlType.Double : SqlType.Numeric], floating ? SqlType.Double : SqlType.Numeric)
    {
        _sample = sample;
        _squareRoot = squareRoot;
    }

    internal bool AcceptsInput(DatabaseType type)
        => (type is DatabaseType.Float32 or DatabaseType.Float64 or DatabaseType.Null)
            ? ReturnType == SqlType.Double : SqlFunctionResolver.IsNumeric(type) && ReturnType == SqlType.Numeric;

    protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context)
        => new Accumulator(_sample, _squareRoot);

    [System.Diagnostics.DebuggerDisplay("Accumulator")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(Accumulator.DebugView))]
    private sealed class Accumulator : SqlAggregateAccumulator
    {
        private static readonly BigInteger _unit = BigInteger.Pow(10, 28);
        private static readonly BigInteger _maximum = (BigInteger.One << 96) - 1;
        private readonly bool _sample;
        private readonly bool _squareRoot;
        private long _count;
        private BigInteger _sum;
        private BigInteger _squares;
        private bool _floating;
        private double _floatSum;
        private double _floatSquares;

        internal Accumulator(bool sample, bool squareRoot)
        {
            _sample = sample;
            _squareRoot = squareRoot;
        }

        protected override void AddCore(scoped in SqlArguments arguments)
        {
            var value = arguments[0];
            try
            {
                long previous = _count;
                _count = checked(_count + 1);
                if (value.Type is DatabaseType.Float32 or DatabaseType.Float64)
                {
                    _floating = true;
                    double number = value.Type == DatabaseType.Float32 ? value.AsSingle() : value.AsDouble();
                    double oldSum = _floatSum;
                    _floatSum += number;
                    // PostgreSQL float.c:3090-3142, Youngs-Cramer transition.
                    if (previous > 0)
                    {
                        double delta = number * _count - _floatSum;
                        _floatSquares += delta * delta / ((double)_count * previous);
                        if (double.IsInfinity(_floatSum) || double.IsInfinity(_floatSquares))
                        {
                            if (!double.IsInfinity(oldSum) && !double.IsInfinity(number)) { throw new OverflowException(); }
                            _floatSquares = double.NaN;
                        }
                    }
                    else if (!double.IsFinite(number)) { _floatSquares = double.NaN; }
                    return;
                }

                decimal numberExact = value.Type switch
                {
                    DatabaseType.Int8 => value.AsSByte(),
                    DatabaseType.Int16 => value.AsInt16(),
                    DatabaseType.Int32 => value.AsInt32(),
                    DatabaseType.Int64 => value.AsInt64(),
                    _ => value.AsDecimal(),
                };
                Span<int> bits = stackalloc int[4];
                decimal.GetBits(numberExact, bits);
                BigInteger scaled = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
                scaled *= BigInteger.Pow(10, 28 - ((bits[3] >> 16) & 0xff));
                if (bits[3] < 0) { scaled = -scaled; }
                _sum += scaled;
                _squares += scaled * scaled;
            }
            catch (OverflowException exception)
            {
                throw SqlEvaluationException.NumericValueOutOfRange(ErrorMessages.StatisticsOutOfRange, exception);
            }
        }

        protected override SqlValue FinishCore()
        {
            if (_count == 0 || _sample && _count == 1) { return SqlValue.Null; }
            if (_floating)
            {
                double variance = _floatSquares / (_sample ? _count - 1 : _count);
                return SqlValue.FromDouble(_squareRoot ? Math.Sqrt(variance) : variance);
            }

            // numeric.c:6060-6139: (N * sum(x*x) - sum(x)^2) / (N * (N-1 or N)).
            // Values are integers at scale 28; neither transition nor cancellation rounds.
            BigInteger numerator = _count * _squares - _sum * _sum;
            BigInteger denominator = (BigInteger)_count * (_sample ? _count - 1 : _count) * _unit * _unit;
            for (int scale = 28; scale >= 0; scale--)
            {
                BigInteger factor = BigInteger.Pow(10, scale);
                BigInteger coefficient;
                if (_squareRoot)
                {
                    BigInteger radicand = numerator * factor * factor;
                    coefficient = IntegerSquareRoot(radicand / denominator);
                    // Compare the exact root with coefficient + 1/2, avoiding an
                    // intermediate rounded variance or a floating-point square root.
                    int rounding = (radicand * 4).CompareTo(denominator * BigInteger.Pow(coefficient * 2 + 1, 2));
                    if (rounding >= 0) { coefficient++; }
                }
                else
                {
                    coefficient = BigInteger.DivRem(numerator * factor, denominator, out var remainder);
                    int rounding = (remainder * 2).CompareTo(denominator);
                    if (rounding >= 0) { coefficient++; }
                }
                if (coefficient > _maximum) { continue; }
                return SqlValue.FromDecimal(new decimal((int)(uint)(coefficient & uint.MaxValue),
                    (int)(uint)((coefficient >> 32) & uint.MaxValue), (int)(uint)(coefficient >> 64), false, (byte)scale));
            }
            throw SqlEvaluationException.NumericValueOutOfRange(ErrorMessages.StatisticsOutOfRange);
        }

        private static BigInteger IntegerSquareRoot(BigInteger value)
        {
            if (value.IsZero) { return value; }
            BigInteger root = BigInteger.One << (int)((value.GetBitLength() + 1) / 2);
            while (true)
            {
                BigInteger next = (root + value / root) / 2;
                if (next >= root) { return root; }
                root = next;
            }
        }

        private sealed class DebugView
        {
            public DebugView(Accumulator value) { _ = value; }
        }
    }

    private sealed class DebugView
    {
        private readonly SqlStatisticsFunction _target;
        public DebugView(SqlStatisticsFunction value) { _target = value; }
        public string Name => _target.Name;
        public SqlType ReturnType => _target.ReturnType;
    }
}
