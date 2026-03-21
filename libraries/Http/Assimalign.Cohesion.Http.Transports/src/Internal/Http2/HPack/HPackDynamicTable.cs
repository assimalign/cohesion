using System;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http2.HPack;

internal sealed class HPackDynamicTable
{
    private HPackHeaderField[] _buffer;
    private int _maxSize;
    private int _size;
    private int _count;
    private int _insertIndex;
    private int _removeIndex;

    public HPackDynamicTable(int maxSize)
    {
        _buffer = new HPackHeaderField[Math.Max(1, maxSize / HPackHeaderField.RfcOverhead)];
        _maxSize = maxSize;
    }

    public int Count => _count;

    public int MaxSize => _maxSize;

    public ref readonly HPackHeaderField this[int index]
    {
        get
        {
            if ((uint)index >= _count)
            {
                throw new IndexOutOfRangeException();
            }

            index = _insertIndex - index - 1;

            if (index < 0)
            {
                index += _buffer.Length;
            }

            return ref _buffer[index];
        }
    }

    public void Insert(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        Insert(staticTableIndex: null, name, value);
    }

    public void Insert(int? staticTableIndex, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        int entryLength = HPackHeaderField.GetLength(name.Length, value.Length);
        EnsureAvailable(entryLength);

        if (entryLength > _maxSize)
        {
            return;
        }

        _buffer[_insertIndex] = new HPackHeaderField(staticTableIndex, name, value);
        _insertIndex = (_insertIndex + 1) % _buffer.Length;
        _size += entryLength;
        _count++;
    }

    public void Resize(int maxSize)
    {
        if (maxSize > _maxSize)
        {
            HPackHeaderField[] newBuffer = new HPackHeaderField[Math.Max(1, maxSize / HPackHeaderField.RfcOverhead)];
            int headCount = Math.Min(_buffer.Length - _removeIndex, _count);
            int tailCount = _count - headCount;

            Array.Copy(_buffer, _removeIndex, newBuffer, 0, headCount);
            Array.Copy(_buffer, 0, newBuffer, headCount, tailCount);

            _buffer = newBuffer;
            _removeIndex = 0;
            _insertIndex = _count;
            _maxSize = maxSize;
        }
        else
        {
            _maxSize = maxSize;
            EnsureAvailable(0);
        }
    }

    private void EnsureAvailable(int available)
    {
        while (_count > 0 && _maxSize - _size < available)
        {
            ref HPackHeaderField field = ref _buffer[_removeIndex];
            _size -= field.Length;
            field = default;
            _count--;
            _removeIndex = (_removeIndex + 1) % _buffer.Length;
        }
    }
}
