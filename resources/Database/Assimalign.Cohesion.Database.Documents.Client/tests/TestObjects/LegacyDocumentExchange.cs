using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

// The pre-RETURNING client's Execute completion predicate, kept as a wire compatibility probe.
internal sealed class LegacyDocumentExchange : DatabaseProtocolExchange<long>
{
    private readonly string _statement;

    internal LegacyDocumentExchange(string statement) : base(DocumentProtocol.Family, transactionStatus: true)
    {
        _statement = statement;
    }

    protected override async ValueTask<long> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer,
        CancellationToken cancellationToken)
    {
        await writer.WriteFrameAsync(new((ProtocolMessageType)DocumentProtocolMessageType.Execute,
            new DocumentProtocolExecuteMessage(_statement, "{}"u8.ToArray()).Encode()), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        bool header = false;
        long count = 0;
        while (true)
        {
            var frame = await reader.ReadFrameAsync(cancellationToken) ?? throw new ProtocolException("Incomplete legacy response.");
            switch ((byte)frame.Type)
            {
                case (byte)DocumentProtocolMessageType.ResultHeader:
                    _ = DocumentProtocolResultHeaderMessage.Decode(frame.Payload.Span);
                    header = true;
                    break;
                case (byte)DocumentProtocolMessageType.Document:
                    _ = DocumentProtocolResultMessage.Decode(frame.Payload.Span);
                    count++;
                    break;
                case (byte)DocumentProtocolMessageType.Complete:
                    var complete = DocumentProtocolCompleteMessage.Decode(frame.Payload.Span);
                    bool valid = header ? complete.AffectedCount == -1 : count == 0 && complete.AffectedCount >= 0;
                    if (complete.ResultCount != count || !valid) { throw new ProtocolException("Legacy completion rejected."); }
                    return complete.AffectedCount;
                default: throw new ProtocolException("Unexpected legacy response.");
            }
        }
    }
}
