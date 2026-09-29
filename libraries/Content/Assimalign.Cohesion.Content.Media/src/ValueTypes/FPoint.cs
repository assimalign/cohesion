using System;

namespace Assimalign.IO;

public readonly struct FPoint
{
    private readonly uint _value;

    public FPoint(uint value)
    {
        this._value = value;
    }

    public uint Value => _value;

    public double ToDouble()
    {
        return (double)_value / 65536;
    }


    public static FPoint FromDouble(double value) => new FPoint((uint)Math.Round(value * 65536, 0));

    public static implicit operator FPoint(double value) => FromDouble(value);

    public static implicit operator FPoint(uint value) => new FPoint(value);
}
