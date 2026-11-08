# NPPAI

Local AI writing autocomplete for x64 Notepad++ on Windows, using Ollama and .NET 8 with a DNNE native shim.

## Run

Run `.\scripts\Start-Local.ps1` from PowerShell. It builds and opens a separate local Notepad++ copy in `.local\Notepad++`. Close that local copy before updating its plugin. Building requires the .NET 8 SDK, Visual Studio C++ Build Tools and Windows SDK. Running requires the .NET 8 Desktop Runtime and Ollama with `qwen2.5:3b` installed.

For everyday use, run `.\scripts\Create-Shortcut.ps1` once and open **Notepad++ with NPPAI** from your desktop. The ordinary installed Notepad++ shortcut opens a different installation and will not show NPPAI unless you separately install the package there. The dedicated shortcut launches directly without rebuilding and keeps the local copy's normal session behaviour.

Type at the end of a line and pause, including partway through a word. For example, `I will inves` can show `tigate the issue.` directly after the caret. Grey ghost text appears without modifying the document. Tab accepts all, Ctrl+Right accepts one word, and Escape dismisses. Acceptance is an undo action. Selections, multiple cursors and read-only documents are excluded.

The SOC profile uses ordinary greeting examples for short greetings such as `hello`, `hel`, or `Hi`, while retaining SOC guidance for investigation notes. Model responses use a structured `text` field containing the completed current word and the continuation together, preserving the word boundary. Incomplete JSON is not displayed or accepted; a short response appears once its object is complete. Suggestions are still restricted to the end of a line.

Open **Plugins → NPPAI → Settings and models** to select installed prediction and correction models, choose the SOC or general writing profile, set suggestion length, refresh the model list, change the loopback Ollama endpoint and debounce, or control learning and retrieval independently. Prediction defaults: `qwen2.5:3b`, SOC profile, 250 ms debounce, 1,000 context characters (maximum 4,000), six prediction words. The SOC profile favours concise investigation notes, preserves uncertainty, and filters new named entities. Predictions still need explicit Tab acceptance and are not evidence of security findings. Status shows latency, correction counts, memory counts and connection errors. Other settings can be changed in the JSON file, followed by a restart.

Automatic spelling correction uses a separate background pass after a 1.5-second pause, with `qwen3.5:9b` by default. Install that model through Ollama or select another installed model in settings. A blank correction model uses the prediction model. The pass queues recent sentences (up to 4,000 recent characters / 16 sentence snapshots), so moving to the next line does not abandon earlier mistakes. Disjoint edits move queued byte ranges; overlapping edits invalidate the affected snapshot. Document identity and the exact original range are checked again before applying anything. The word at the caret is left alone until you type a boundary. Undo/redo clears pending corrections and pauses them until a direct user edit.

Correction also scans the remaining text on the current line when the caret is at its start or middle; existing spelling errors to the right of the caret are not excluded.

Correction work continues when the editor loses focus, the window moves/resizes, or Status is opened. These UI events only hide predictions. Document changes, source edits, Undo and shutdown still invalidate or cancel corrections where appropriate. Provider timeouts release the worker and are reported in Status instead of silently leaving it stuck. Status includes the current correction phase, started/finished/cancelled requests, request duration, skipped proposals and the last result, so zero applied edits can be distinguished from a pending request or rejected proposals.

The model returns structured, separate spelling proposals. Invalid or ambiguous proposals no longer discard their valid siblings. Bounded spacing/split-word repairs and adjacent-letter transpositions are supported. Acronyms, protected SOC terms, numbers, hostnames, URLs, email addresses, product/name casing inside sentences, and common certainty/verdict words are protected. These checks are conservative, not a proof of semantic correctness: some mistakes are skipped, and AI spelling decisions can still be wrong. Ctrl+Z undoes each applied sentence batch as one action. Corrections are not recorded as user-accepted predictions. Broader grammar rewrites and ambiguous text such as `SOusers` remain unchanged.

Two-letter misspellings such as `se` → `see` can receive a one-edit repair. Existing short function words and certainty/verdict terms remain protected.

Add exact names or specialised terms to the `protected_terms` array in the JSON configuration if needed. Explicit dictionary entries are also protected. Automatically learned capitalised vocabulary is useful for prediction but is not automatically trusted as a spelling exception, since it may itself contain typos.

Disable **Automatically correct completed words** independently of predictions if desired. `correction_delay_ms` and `correction_timeout_ms` are configurable in the JSON file. Both requests run independently in the plugin, but Ollama and your hardware still share inference resources.

## Local data

Configuration, `memory.db` and error logs live in `%LOCALAPPDATA%\NPPAI`. Accepted suggestions and recent context are stored locally when learning is enabled. Memory retrieval uses trusted phrases, heuristic style and relevant dictionary entries. Phrases need at least 20 acceptances before promotion; a replacement phrase starts its trust count again. Edits inside an accepted completion are marked and excluded from style inference. Disabling learning stops new observations; disabling memory stops retrieval. Existing data is retained.

Only a loopback HTTP endpoint is supported. Redirects and proxies are disabled. There is no cloud service or API key.

## Build and verify

```powershell
.\scripts\Build.ps1
dotnet run --project tests\NPPAI.Smoke -c Release
dotnet run --project tests\NPPAI.Smoke -c Release -- --lifecycle
dotnet run --project tests\NPPAI.Evaluation -c Release -- artifacts/prediction-evaluation.json
```

The smoke harness uses native editor-message fixtures, controlled delayed provider responses, and real requests to both models. It checks independent edits, split-word repairs, range movement and invalidation, in-flight races, UTF-8, predictions, cancellation, undo, trust and persistence. Test learning data is isolated in temporary storage. The evaluation runner compares two models and the old/new prediction configurations; see [the local evaluation report](docs/prediction-evaluation.md) for results and limitations.

`--lifecycle` runs the deterministic unit/editor/focus/timeout checks without requiring Ollama. GitHub Actions runs this mode and builds an x64 native plugin package on Windows. The full smoke suite and model evaluation need both installed models and are run locally.

The package is in `artifacts\NPPAI`. To install elsewhere, close that x64 Notepad++ instance and copy **all package files** into its `plugins\NPPAI` folder. The native `NPPAI.dll`, managed DLL, runtime configuration and dependencies must stay together. Program Files installation may require an administrator.

## Remaining limits

Latency depends on hardware, model size and cold loading; the plan's 300 ms target is not guaranteed. Suggestions stream as text arrives. The overlay uses Consolas and a single line, clipped to the editor edge. Visual verification across DPI, themes and split views remains needed. Adaptation over several days requires real usage. Broader grammar rewrites, SOC templates and document RAG are phase-two features.
