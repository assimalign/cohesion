using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests.TestObjects;

internal static class OqlWindowAcceptanceRecorder
{
    internal static (int Sources, int Candidates, int Accepted, int Excluded) Slice(string statements, byte[] bitmap,
        string destination, Func<string, int, bool> keep, bool inputExcludesReserved = false)
    {
        var flags = new List<byte>();
        var reservedOrdinals = new List<int>();
        int original = 0, sources = 0, candidates = 0, accepted = 0, excluded = 0, ordinal = 0;
        int selectedOrdinal = 0;
        using var input = new StreamReader(new GZipStream(File.OpenRead(statements), CompressionMode.Decompress));
        using var writer = new StreamWriter(new GZipStream(File.Create(Path.Combine(destination, "Statements.jsonl.gz")), CompressionLevel.SmallestSize));
        while (input.ReadLine() is string line)
        {
            using var json = JsonDocument.Parse(line);
            string source = json.RootElement.GetString()!;
            bool selected = keep(line, ordinal++);
            if (selected) { writer.WriteLine(line); sources++; }
            foreach (string text in OqlWindowFreeCorpus.Candidates(source))
            {
                bool reserved = OqlWindowFreeCorpus.HasReservedCall(text);
                if (selected)
                {
                    if (reserved) { reservedOrdinals.Add(selectedOrdinal); }
                    selectedOrdinal++;
                }
                if (reserved && inputExcludesReserved) { if (selected) { excluded++; } continue; }
                bool required = (bitmap[original / 8] & (1 << (original % 8))) != 0;
                original++;
                if (!selected) { continue; }
                if (reserved) { excluded++; continue; }
                if (candidates % 8 == 0) { flags.Add(0); }
                if (required) { flags[^1] |= (byte)(1 << (candidates % 8)); accepted++; }
                candidates++;
            }
        }
        using var output = new GZipStream(File.Create(Path.Combine(destination, "Acceptance.bin.gz")), CompressionLevel.SmallestSize);
        output.Write(flags.ToArray());
        File.WriteAllText(Path.Combine(destination, "ReservedCalls.json"), "[" + string.Join(",", reservedOrdinals) + "]");
        return (sources, candidates, accepted, excluded);
    }

    // Copy this helper and the generators into the reference commit's test project, then
    // invoke from a temporary Fact. No reference to window AST types is needed at base.
    internal static (int Candidates, int Accepted, int Excluded) Record(IEnumerable<string> statements, string destination,
        string? reservedCallsDestination = null)
    {
        var parser = new OqlQueryParser(true);
        var flags = new List<byte>();
        var reservedOrdinals = new List<int>();
        int candidates = 0, accepted = 0, ordinal = 0;
        foreach (string text in statements)
        {
            int current = ordinal++;
            if (reservedCallsDestination is not null && OqlWindowFreeCorpus.HasReservedCall(text))
            { reservedOrdinals.Add(current); continue; }
            if (candidates % 8 == 0) { flags.Add(0); }
            if (!parser.Parse(text).Diagnostics.Any())
            {
                flags[^1] |= (byte)(1 << (candidates % 8));
                accepted++;
            }
            candidates++;
        }
        using var output = new GZipStream(File.Create(destination), CompressionLevel.SmallestSize);
        output.Write(flags.ToArray());
        if (reservedCallsDestination is not null)
        { File.WriteAllText(reservedCallsDestination, "[" + string.Join(",", reservedOrdinals) + "]"); }
        return (candidates, accepted, reservedOrdinals.Count);
    }
}
