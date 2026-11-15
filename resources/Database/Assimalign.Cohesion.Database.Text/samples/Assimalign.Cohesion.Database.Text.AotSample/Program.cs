using System;

namespace Assimalign.Cohesion.Database.Text.AotSample;

internal static class Program
{
    private static int Main()
    {
        int checks = 0;
        TextVector simple = TextAnalyzer.Simple.Analyze("FOX fox 42");
        if (simple.Lexemes.Count == 2 && simple.Lexemes[1].Text == "fox" && simple.Lexemes[1].Positions.Count == 2)
        {
            checks++;
        }

        TextVector english = TextAnalyzer.English.Analyze("the skies running relational");
        if (english.Lexemes.Count == 3 && english.Lexemes[0].Text == "relat" && english.Lexemes[1].Text == "run" && english.Lexemes[2].Text == "sky")
        {
            checks++;
        }

        TextAnalyzer accents = TextAnalyzer.Create(TextAnalyzerDescriptor.Create("simple", 1, true));
        TextVector folded = accents.Analyze("CAFÉ İSTANBUL Σ ς 𐐀");
        if (folded.Lexemes.Count == 4 && folded.Lexemes[0].Text == "cafe" && folded.Lexemes[1].Text == "istanbul"
            && folded.Lexemes[2].Positions.Count == 2 && folded.Lexemes[3].Text == "𐐨")
        {
            checks++;
        }

        try
        {
            TextAnalyzer.Create(TextAnalyzerDescriptor.Create("english", 2));
        }
        catch (TextException error) when (error.Code == TextErrorCode.AnalyzerVersionMismatch)
        {
            checks++;
        }

        TextVector value = TextVector.Create([TextLexeme.Create("fox", [TextPosition.Create(20000, TextWeight.A)])]);
        if (value.Lexemes[0].Positions[0] == TextPosition.Create(16383, TextWeight.A)
            && value.Equals(TextVector.Create(value.Lexemes)))
        {
            checks++;
        }

        Console.WriteLine($"Database.Text NativeAOT: {checks}/5 checks passed.");
        return checks == 5 ? 0 : 1;
    }
}
