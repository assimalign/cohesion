using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

// One physical line per node, even with hostile FourCCs or field values. Borrows its output.
[global::System.Diagnostics.DebuggerDisplay("BmffInspectionWriter")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class BmffInspectionWriter : TextWriter
{
    private readonly TextWriter _writer;
    internal BmffInspectionWriter(TextWriter writer) => _writer = writer;
    public override Encoding Encoding => _writer.Encoding;
    public override IFormatProvider FormatProvider => CultureInfo.InvariantCulture;
    public override void Write(char value)
    {
        switch (value)
        {
            case '\n': _writer.Write("\\n"); break;
            case '\r': _writer.Write("\\r"); break;
            case '\t': _writer.Write("\\t"); break;
            default:
                if (char.IsControl(value)) { _writer.Write("\\x" + ((int)value).ToString("X2", CultureInfo.InvariantCulture)); }
                else { _writer.Write(value); }
                break;
        }
    }
    public override void Write(string? value)
    {
        if (value is not null) { Write(value.AsSpan()); }
    }
    public override void Write(ReadOnlySpan<char> buffer)
    {
        foreach (var value in buffer) { Write(value); }
    }

    private sealed class DebugView
    {
        private readonly BmffInspectionWriter _target;

        public DebugView(BmffInspectionWriter value)
        {
            _target = value;
        }

        public Encoding Encoding => _target.Encoding;
        public IFormatProvider FormatProvider => _target.FormatProvider;
        public char[] CoreNewLine => _target.CoreNewLine;
        public string NewLine => _target.NewLine;
    }
}
