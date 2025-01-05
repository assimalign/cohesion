using System;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal sealed class HPackDynamicTable
{
    private HPackHeaderField[] buffer;
    private int maxSize;
    private int size;
    private int count;
    private int insertIndex;
    private int removeIndex;

    public HPackDynamicTable(int maxSize)
    {
        this.buffer = new HPackHeaderField[maxSize / HPackHeaderField.RfcOverhead];
        this.maxSize = maxSize;
    }

    public int Count => count;

    public int Size => size;

    public int MaxSize => maxSize;

    public ref readonly HPackHeaderField this[int index]
    {
        get
        {
            if (index >= count)
            {
                throw new IndexOutOfRangeException();
            }

            index = insertIndex - index - 1;

            if (index < 0)
            {
                // _buffer is circular; wrap the index back around.
                index += buffer.Length;
            }

            return ref buffer[index];
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

        if (entryLength > maxSize)
        {
            // http://httpwg.org/specs/rfc7541.html#rfc.section.4.4
            // It is not an error to attempt to add an entry that is larger than the maximum size;
            // an attempt to add an entry larger than the maximum size causes the table to be emptied
            // of all existing entries and results in an empty table.
            return;
        }

        var entry = new HPackHeaderField(staticTableIndex, name, value);
        buffer[insertIndex] = entry;
        insertIndex = (insertIndex + 1) % buffer.Length;
        size += entry.Length;
        count++;
    }

    public void Resize(int maxSize)
    {
        if (maxSize > this.maxSize)
        {
            var newBuffer = new HPackHeaderField[maxSize / HPackHeaderField.RfcOverhead];

            int headCount = Math.Min(buffer.Length - removeIndex, count);
            int tailCount = count - headCount;

            Array.Copy(buffer, removeIndex, newBuffer, 0, headCount);
            Array.Copy(buffer, 0, newBuffer, headCount, tailCount);

            buffer = newBuffer;
            removeIndex = 0;
            insertIndex = count;
            this.maxSize = maxSize;
        }
        else
        {
            this.maxSize = maxSize;
            EnsureAvailable(0);
        }
    }

    private void EnsureAvailable(int available)
    {
        while (count > 0 && maxSize - size < available)
        {
            ref HPackHeaderField field = ref buffer[removeIndex];
            size -= field.Length;
            field = default;

            count--;
            removeIndex = (removeIndex + 1) % buffer.Length;
        }
    }
}