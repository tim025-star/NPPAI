# Local prediction evaluation

Historical benchmark: these measurements describe the initial plain-text SOC profile. The later greeting fix uses structured prediction responses and adaptive greeting examples, so these timings do not describe the current protocol. Greeting reproduction/fix traces are in `artifacts/hello-diagnosis.json` and `artifacts/hello-fixed.json`; a follow-up raw SOC run is in `artifacts/prediction-evaluation-structured.json`. That follow-up overlapped integration tests and is unsuitable for an isolated latency comparison. The qualitative limitations below still apply.

Evaluated 10 SOC note fragments with `qwen2.5:3b` and `qwen3.5:9b`, before and after the SOC prompt/profile changes. Each model was warmed first. Temperature was zero. The baseline allowed 20 words / 80 generated tokens; the SOC configuration allowed six words / 32 tokens, with style examples and named-entity filtering. These results compare the complete configurations, not the prompt in isolation.

Run: `dotnet run --project tests/NPPAI.Evaluation -c Release -- artifacts/prediction-evaluation-heldout.json`

Raw contexts, responses, accepted suffixes and timing are saved in that JSON file. The examples in the final prompt have different input text from the evaluation fragments, but both cover the same SOC domain. This is a small development evaluation, not an independent production acceptance study.

The historical raw benchmark is committed at [evaluation/prediction-evaluation-plain-text.json](evaluation/prediction-evaluation-plain-text.json). A later greeting-fix trace is in [evaluation/greeting-fix.json](evaluation/greeting-fix.json).

| Model/configuration | Median first usable text | Median completed response |
|---|---:|---:|
| 3B, generic baseline | 28.5 ms | 59.5 ms |
| 3B, SOC + short phrases | 32.5 ms | 71.5 ms |
| 9B, generic baseline | 98 ms | 178 ms |
| 9B, SOC + short phrases | 291.5 ms | 365.5 ms |

Timing excludes the 250 ms UI debounce, cold model loading, and background correction contention. First usable text means a nonempty filtered candidate, not a guarantee that it was semantically good. Empty final suggestions are still possible even after an early candidate appeared.

## Qualitative review

Criteria: grammatical fit, relevance to the supplied note, no new unsupported factual finding, and no repetition. These judgments are manual, not automated quality scores.

- For a UK user who likely uses a network tool, the generic baseline suggested **UK keyboard** (3B) and **British plug socket** (9B). The SOC configuration suggested **proxy or VPN** from both.
- After three-country sign-ins and “This may indicate”, the baseline suggested **global expansion strategy** / **a global user base**. SOC produced **proxy use or travel** / **travel or proxy use**, preserving the hypothetical framing.
- In the user-confirmation fragment, the baseline discussed accepting terms / wanting to proceed. SOC asked whether the user initiated the sign-ins / authorised the sessions.
- Both SOC configurations completed an unfinished word with **using a proxy**, while the generic variants were off-topic or empty.
- Weak outputs remain: 3B produced **similar** after “logs look”; 9B produced **normal for the user**, which assumes a baseline absent from the input. Both produced **multiple devices** after a location-oriented “log in from” fragment.

Decision: retain **qwen2.5:3b for predictions** on this machine. The larger model was materially slower and did not consistently eliminate the remaining quality problems. Keep **qwen3.5:9b for correction**. Both choices remain configurable. Six-word suggestions and the SOC profile are the new defaults; the general profile remains available.

Continue collecting representative, de-identified fragments and acceptance/rejection observations before making a broader quality claim. Neither the model nor the named-entity guard proves that a continuation is factually correct; predictions remain explicit Tab acceptance.
