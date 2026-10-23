namespace Assimalign.Cohesion.Content.Pdf.Internal;

[global::System.Diagnostics.DebuggerDisplay("PdfSecurityState")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class PdfSecurityState
{
    internal bool IsEncrypted { get; set; }

    private sealed class DebugView
    {
        public DebugView(PdfSecurityState value)
        {
        }
    }
}
