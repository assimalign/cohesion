using Microsoft.VisualBasic;
using System;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// 
/// </summary>
public readonly unsafe struct Page
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="pointer"></param>
    public Page(byte* pointer)
    {
        Pointer = pointer;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="bytes"></param>
    public Page(byte[] bytes)
    {
        fixed (byte* ptr = bytes)
        {
            Pointer = ptr;
        }
    }


    public const int Size = 8192;

    /// <summary>
    /// 
    /// </summary>
    public readonly byte* Pointer;

    #region Page Header

    /// <summary>
    /// 
    /// </summary>
    public long Id
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            return ((Header*)Pointer)->PageId;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            ((Header*)Pointer)->PageId = value;
        }
    }

    /// <summary>
    /// 
    /// </summary>
    public PageType Type { get; }

    /// <summary>
    /// 
    /// </summary>
    public PageFlags Flags { get; }

    public bool IsOverflow
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return (((Header*)Pointer)->Flags & PageFlags.Overflow) == PageFlags.Overflow; }
    }

    public int OverflowSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get { return ((Header*)Pointer)->OverflowSize; }
        set { ((Header*)Pointer)->OverflowSize = value; }
    }

    #endregion



    public Span<byte> AsSpan()
    {
        return new Span<byte>(Pointer, IsOverflow ? OverflowSize + 96 : Size);
    }


    [StructLayout(LayoutKind.Explicit, Size = 96, Pack = 1)]
    unsafe partial struct Header
    {
        [FieldOffset(0)]
        public long PageId;

        [FieldOffset(8)]
        public int OverflowSize;

        [FieldOffset(12)]
        public PageFlags Flags;

        [FieldOffset(22)]
        public fixed byte Reserved1[9];

        [FieldOffset(4)] // used only if we aren't using crypto
        public short Checksum;

        [FieldOffset(32)]// used only when using crypto
        public fixed byte Nonce[16];

        [FieldOffset(48)]
        public fixed byte Mac[16];
    }
}
