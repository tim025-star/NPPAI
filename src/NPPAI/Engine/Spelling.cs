using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NPPAI.Engine;

public record SpellingEdit(int Start, int Length, string Replacement);
public record SpellingProposal(string Original, string Replacement);

public static class Spelling
{
    public static readonly string[] ProtectedTerms = { "FP", "TP", "NFA", "BAU", "SOC", "Entra", "MFA", "IPs", "IP", "AU", "NZ", "UK", "RBNZ", "Palo", "CrowdStrike", "Sentinel", "CyberArk" };
    static readonly HashSet<string> MeaningWords = new("not no yes true false clean malicious benign likely unlikely confirmed possible impossible allowed denied passed failed safe unsafe proxy VPN an as at be by do go he if in is it me my of on or so to up us we".Split(' '), StringComparer.OrdinalIgnoreCase);
    public static readonly JsonElement Format = JsonDocument.Parse("""
        {"type":"object","properties":{"edits":{"type":"array","maxItems":16,"items":{"type":"object","properties":{"original":{"type":"string"},"replacement":{"type":"string"}},"required":["original","replacement"],"additionalProperties":false}}},"required":["edits"],"additionalProperties":false}
        """).RootElement.Clone();

    public static List<SpellingProposal> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<SpellingProposal>();
        foreach (var item in doc.RootElement.GetProperty("edits").EnumerateArray().Take(16))
            if (item.TryGetProperty("original", out var a) && item.TryGetProperty("replacement", out var b) && a.ValueKind == JsonValueKind.String && b.ValueKind == JsonValueKind.String)
                result.Add(new(a.GetString(), b.GetString()));
        return result;
    }

    public static List<SpellingEdit> ValidateEdits(string original, IEnumerable<SpellingProposal> proposals, int caretCharacter, IEnumerable<string> protectedTerms = null)
    {
        var result = new List<SpellingEdit>();
        var glossary = new HashSet<string>(ProtectedTerms.Concat(protectedTerms ?? Array.Empty<string>()), StringComparer.OrdinalIgnoreCase);
        var protectedSpans = Regex.Matches(original, @"\S*(?:https?://|@|[/\\]|\.[A-Za-z]{2,}|\d)\S*");
        foreach (var proposal in proposals.Take(16))
        {
            string from = proposal.Original ?? "", to = proposal.Replacement ?? "";
            if (from == to || from.Length < 2 || from.Length > 60 || to.Length < 2 || to.Length > 60 || !Regex.IsMatch(from, @"^[\p{L}\p{M}]+(?: [\p{L}\p{M}]+){0,2}$") || !Regex.IsMatch(to, @"^[\p{L}\p{M}]+(?: [\p{L}\p{M}]+){0,2}$")) continue;
            string compactFrom = from.Replace(" ", ""), compactTo = to.Replace(" ", "");
            if (char.IsUpper(from[0]) != char.IsUpper(to[0]) || Distance(compactFrom.ToLowerInvariant(), compactTo.ToLowerInvariant()) > (compactFrom.Length == 2 ? 1 : compactFrom.Length < 5 ? 2 : 3)) continue;
            var words = from.Split(' ');
            if (words.Any(w => glossary.Contains(w) || MeaningWords.Contains(w) || w.All(char.IsUpper) || w.Skip(1).Any(char.IsUpper))) continue;
            foreach (Match match in Regex.Matches(original, @"(?<![\p{L}\p{M}\p{N}_])" + Regex.Escape(from) + @"(?![\p{L}\p{M}\p{N}_])"))
            {
                int start = match.Index, end = start + match.Length;
                if (caretCharacter > start && caretCharacter <= end) continue;
                if (protectedSpans.Any(p => start < p.Index + p.Length && end > p.Index)) continue;
                // Capitalised names inside a sentence require explicit user review.
                bool sentenceStart = start == 0 || Regex.IsMatch(original.Substring(0,start), @"(?:^|[.!?\r\n])\s*$");
                if (!sentenceStart && char.IsUpper(from[0])) continue;
                result.Add(new(start, match.Length, to));
            }
        }
        var unique = result.Distinct().ToList();
        // Conflicting proposals are all discarded; unrelated valid edits survive.
        return unique.Where(a => !unique.Any(b => a != b && a.Start < b.Start+b.Length && a.Start+a.Length > b.Start)).OrderBy(a => a.Start).ToList();
    }

    static int Distance(string a, string b)
    {
        int[] previous = Enumerable.Range(0,b.Length+1).ToArray();
        int[] beforePrevious = previous;
        for (int i=1;i<=a.Length;i++)
        {
            int[] next = new int[b.Length+1]; next[0]=i;
            for (int j=1;j<=b.Length;j++)
            {
                next[j]=Math.Min(Math.Min(next[j-1]+1,previous[j]+1),previous[j-1]+(a[i-1]==b[j-1]?0:1));
                if(i>1 && j>1 && a[i-1]==b[j-2] && a[i-2]==b[j-1]) next[j]=Math.Min(next[j],beforePrevious[j-2]+1);
            }
            beforePrevious=previous;
            previous=next;
        }
        return previous[b.Length];
    }
}
