using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Dns;

public class DnsDatagram
{
    #region variables

    public const ushort EDNS_DEFAULT_UDP_PAYLOAD_SIZE = 1232;
    public const ushort EDNS_MAX_UDP_PAYLOAD_SIZE = 4096;

    const int MAX_XFR_RESPONSE_SIZE = 16384; //since the compressed name pointer offset can only address 16384 bytes in datagram

    DnsDatagramMetadata _metadata;
    DnsDatagramEdns _edns;
    List<EDnsExtendedDnsErrorOptionData> _dnsClientExtendedErrors;
    bool _shadowHideECSOption;
    EDnsClientSubnetOptionData _shadowECSOption;

    int _size = -1;
    byte[] _parsedDatagramUnsigned;

    ushort _ID;

    byte _QR;
    DnsOpCode _OPCODE;
    byte _AA;
    byte _TC;
    byte _RD;
    byte _RA;
    byte _Z;
    byte _AD;
    byte _CD;
    DnsResponseCode _RCODE;

    IReadOnlyList<DnsQuestionRecord> _question;
    IReadOnlyList<DnsResourceRecord> _answer;
    IReadOnlyList<DnsResourceRecord> _authority;
    IReadOnlyList<DnsResourceRecord> _additional;

    Exception _parsingException;

    DnsDatagram _nextDatagram; //used for TCP XFR multiple messages

    #endregion
    #region properties

    public DnsDatagramMetadata Metadata
    { get { return _metadata; } }

    public DnsDatagramEdns EDNS
    { get { return _edns; } }

    public IReadOnlyList<EDnsExtendedDnsErrorOptionData> DnsClientExtendedErrors
    {
        get
        {
            if (_dnsClientExtendedErrors is null)
                return Array.Empty<EDnsExtendedDnsErrorOptionData>();

            return _dnsClientExtendedErrors;
        }
    }

    public ushort Identifier
    { get { return _ID; } }

    public bool IsResponse
    { get { return _QR == 1; } }

    public DnsOpcode OPCODE
    { get { return _OPCODE; } }

    public bool AuthoritativeAnswer
    { get { return _AA == 1; } }

    public bool Truncation
    { get { return _TC == 1; } }

    public bool RecursionDesired
    { get { return _RD == 1; } }

    public bool RecursionAvailable
    { get { return _RA == 1; } }

    public byte Z
    { get { return _Z; } }

    public bool AuthenticData
    { get { return _AD == 1; } }

    public bool CheckingDisabled
    { get { return _CD == 1; } }

    public DnsResponseCode RCODE
    {
        get
        {
            if (_edns is not null)
                return _edns.ExtendedRCODE;

            return _RCODE;
        }
    }

    public IReadOnlyList<DnsQuestionRecord> Question
    { get { return _question; } }

    public IReadOnlyList<DnsResourceRecord> Answer
    { get { return _answer; } }

    public IReadOnlyList<DnsResourceRecord> Authority
    { get { return _authority; } }

    public IReadOnlyList<DnsResourceRecord> Additional
    { get { return _additional; } }

    public bool IsSigned
    { get { return (_additional.Count > 0) && (_additional[_additional.Count - 1].Type == DnsResourceRecordType.TSIG); } }

    public DnsTsigError TsigError
    {
        get
        {
            if ((_additional.Count > 0) && (_additional[_additional.Count - 1].RDATA is DnsTSIGRecordData tsig))
                return tsig.Error;

            return DnsTsigError.NoError;
        }
    }

    public string TsigKeyName
    {
        get
        {
            if ((_additional.Count > 0) && (_additional[_additional.Count - 1].Type == DnsResourceRecordType.TSIG))
                return _additional[_additional.Count - 1].Name;

            return null;
        }
    }

    public bool IsZoneTransfer
    { get { return (_question.Count > 0) && ((_question[0].Type == DnsResourceRecordType.IXFR) || (_question[0].Type == DnsResourceRecordType.AXFR)); } }

    public Exception ParsingException
    { get { return _parsingException; } }

    public DnsDatagram NextDatagram
    {
        get { return _nextDatagram; }
        set
        {
            if (_nextDatagram is not null)
                throw new InvalidOperationException("Cannot overwrite next datagram.");

            _nextDatagram = value;
        }
    }

    public bool DnssecOk
    {
        get
        {
            if (_edns is null)
                return false;

            return _edns.Flags.HasFlag(EDnsHeaderFlags.DNSSEC_OK);
        }
    }

    public object Tag { get; set; }

    #endregion
}
