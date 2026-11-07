using System;
using System.Collections.Generic;
using System.Text;
using NPPAI.Memory;

namespace NPPAI.Engine
{
    /// <summary>
    /// Builds the system + user prompt for a completion request,
    /// injecting memory (phrases, profile, dictionary) when enabled.
    /// </summary>
    public static class PromptBuilder
    {
        public static List<ChatMessage> Build(
            CollectedContext ctx,
            bool enableMemory,
            List<(string trigger, string text)> phrases,
            (string formality, string length, string greeting, string closing)? profile,
            List<(string term, string expansion)> terms)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are completing text for the user inside Notepad++.");
            sb.AppendLine("Rules:");
            sb.AppendLine("1. Continue naturally from the last cursor position.");
            sb.AppendLine("2. Match the existing tone and register.");
            sb.AppendLine("3. Be concise.");
            sb.AppendLine("4. Predict only what likely comes next.");
            sb.AppendLine("5. Return the continuation only.");
            sb.AppendLine("6. No explanations.");
            sb.AppendLine("7. No markdown.");
            sb.AppendLine("8. Do not repeat the last words already typed.");

            if (enableMemory)
            {
                var prof = profile;
                if (prof.HasValue && (prof.Value.formality != null || prof.Value.length != null))
                {
                    sb.AppendLine();
                    sb.AppendLine("User writing style:");
                    if (prof.Value.formality != null)
                        sb.AppendLine($"- formality: {prof.Value.formality}");
                    if (prof.Value.length != null)
                        sb.AppendLine($"- average sentence length: {prof.Value.length}");
                    if (prof.Value.greeting != null)
                        sb.AppendLine($"- preferred greeting: \"{prof.Value.greeting}\"");
                    if (prof.Value.closing != null)
                        sb.AppendLine($"- preferred closing: \"{prof.Value.closing}\"");
                }

                if (phrases != null && phrases.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("User's established phrases (use these when they fit):");
                    int shown = 0;
                    foreach (var (trigger, text) in phrases)
                    {
                        if (shown >= 5) break;
                        sb.AppendLine($"- \"...{trigger}\" → \"{text}\"");
                        shown++;
                    }
                }

                if (terms != null && terms.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Terms the user uses:");
                    int shown = 0;
                    foreach (var (term, expansion) in terms)
                    {
                        if (shown >= 20) break;
                        sb.AppendLine($"- {term} = {expansion}");
                        shown++;
                    }
                }
            }

            var user = new StringBuilder();
            user.AppendLine("Continue the text below. Output only the continuation, no quotes, no markdown.");
            user.AppendLine();
            user.AppendLine(ctx.Text);

            return new List<ChatMessage>
            {
                new ChatMessage { role = "system", content = sb.ToString() },
                new ChatMessage { role = "user", content = user.ToString() }
            };
        }
    }
}
