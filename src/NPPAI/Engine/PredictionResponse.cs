using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NPPAI.Engine;

// The response carries the completed current word and its continuation together,
// preserving their exact boundary instead of guessing whether a space is missing.
public static class PredictionResponse
{
    public static object Format(string context) => new {
        type="object", properties=new {text=new {type="string",pattern="^"+Regex.Escape(Completion.WordPrefix(context))+@"[^\r\n]*$",maxLength=240}},
        required=new[]{"text"},additionalProperties=false
    };
    public static string Decode(string json,string context,int maxWords,string profile)
    {
        try
        {
            using var doc=JsonDocument.Parse(json);
            if(!doc.RootElement.TryGetProperty("text",out var field) || field.ValueKind!=JsonValueKind.String) return "";
            string text=field.GetString() ?? "";
            if(text.Length>240 || text.Contains('\n') || text.Contains('\r')) return "";
            return Completion.ForProfile(text,context,maxWords,profile);
        }
        catch(JsonException){return "";} // partial JSON cannot be accepted as a completion
    }
    public static string Example(string context,string answer) => JsonSerializer.Serialize(new{text=answer});
}
