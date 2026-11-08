# NPPAI

Self-contained local AI writing autocomplete and spelling correction for x64 Notepad++ on Windows. NPPAI bundles llama.cpp, its GGUF models, the .NET 8 Desktop Runtime and app-local C++ runtime libraries behind a DNNE native shim. No Ollama installation, running service, account or internet connection is needed to use the complete package.

## Run

For everyday use on this machine, open **Notepad++ with NPPAI** from the desktop. The working installation is `.local\Notepad++`. NPPAI starts its bundled inference engines automatically; you do not need to open Ollama or run a build script. Allow the models to load on startup, then type at the end of a line and pause. **Plugins → NPPAI → Status** shows model readiness and request errors.

A complete portable package is available from [GitHub Releases](https://github.com/tim025-star/NPPAI/releases). Download all numbered ZIP parts and `Extract-NPPAI.cmd` into one folder, then run the helper to verify and extract them. Allow about 40 GB temporary free space. The automatic Source code downloads do not contain the models.

To install elsewhere, use the complete `artifacts\NPPAI` folder. Save and close Notepad++, then copy that entire folder to `<Notepad++ directory>\plugins\NPPAI`, or run the included `Install.ps1` with `-NotepadDirectory` pointing to your x64 Notepad++ directory. Installation under Program Files may require an elevated PowerShell window. Reopen Notepad++ normally: NPPAI loads the enabled models in the background. The complete package is approximately 8.8 GB, mostly model weights. Keep all files and subfolders together.

If the desktop shortcut is missing, run `.\scripts\Create-Shortcut.ps1` once. The ordinary installed Notepad++ shortcut opens a different installation and will not show NPPAI unless you separately install the package there. The dedicated shortcut launches directly without rebuilding and keeps the local copy's normal session behaviour.

NPPAI owns its hidden llama-server processes and stops them when Notepad++ closes, including unexpected host termination through a Windows Job Object. Each model uses an authenticated loopback-only endpoint on a dynamically assigned port; redirects and HTTP proxies are disabled. The bundled Vulkan runtime uses a compatible GPU, with a separate CPU runtime as fallback if GPU startup fails. CPU inference can be substantially slower. Models stay loaded while Notepad++ is open. Status reports each model's loading, ready or failed state. Server startup has a two-minute deadline; cancelling a suggestion does not abort a shared model load.

Type at the end of a line and pause, including partway through a word. For example, `I will inves` can show `tigate the issue.` directly after the caret. Grey ghost text appears without modifying the document. Tab accepts all, Ctrl+Right accepts one word, and Escape dismisses. Acceptance is an undo action. Selections, multiple cursors and read-only documents are excluded.

The SOC profile uses ordinary greeting examples for short greetings such as `hello`, `hel`, or `Hi`, while retaining SOC guidance for investigation notes. Model responses use a structured `text` field containing the completed current word and the continuation together, preserving the word boundary. Incomplete JSON is not displayed or accepted; a short response appears once its object is complete. Suggestions are still restricted to the end of a line.

Open **Plugins → NPPAI → Settings and models** to select bundled prediction and correction models, choose the SOC or general writing profile, set suggestion length, change debounce, or control learning and retrieval independently. Prediction defaults: `qwen2.5:3b`, SOC profile, 250 ms debounce, 1,000 context characters (maximum 4,000), six prediction words. The SOC profile favours concise investigation notes, preserves uncertainty, and filters new named entities. Predictions still need explicit Tab acceptance and are not evidence of security findings. Status shows latency, correction counts, memory counts and connection errors. Other settings can be changed in the JSON file, followed by a restart.

Automatic spelling correction uses a separate background pass after a 1.5-second pause, with `qwen3.5:9b` by default. Both default models are included; select another bundled model in settings if desired. A blank correction model uses the prediction model. The pass queues recent sentences (up to 4,000 recent characters / 16 sentence snapshots), so moving to the next line does not abandon earlier mistakes. Disjoint edits move queued byte ranges; overlapping edits invalidate the affected snapshot. Document identity and the exact original range are checked again before applying anything. The word at the caret is left alone until you type a boundary. Undo/redo clears pending corrections and pauses them until a direct user edit.

Correction also scans the remaining text on the current line when the caret is at its start or middle; existing spelling errors to the right of the caret are not excluded.

Correction work continues when the editor loses focus, the window moves/resizes, or Status is opened. These UI events only hide predictions. Document changes, source edits, Undo and shutdown still invalidate or cancel corrections where appropriate. Provider timeouts release the worker and are reported in Status instead of silently leaving it stuck. Status includes the current correction phase, started/finished/cancelled requests, request duration, skipped proposals and the last result, so zero applied edits can be distinguished from a pending request or rejected proposals.

The model returns structured, separate spelling proposals. Invalid or ambiguous proposals no longer discard their valid siblings. Bounded spacing/split-word repairs and adjacent-letter transpositions are supported. Acronyms, protected SOC terms, numbers, hostnames, URLs, email addresses, product/name casing inside sentences, and common certainty/verdict words are protected. These checks are conservative, not a proof of semantic correctness: some mistakes are skipped, and AI spelling decisions can still be wrong. Ctrl+Z undoes each applied sentence batch as one action. Corrections are not recorded as user-accepted predictions. Broader grammar rewrites and ambiguous text such as `SOusers` remain unchanged.

Two-letter misspellings such as `se` → `see` can receive a one-edit repair. Existing short function words and certainty/verdict terms remain protected.

Add exact names or specialised terms to the `protected_terms` array in the JSON configuration if needed. Explicit dictionary entries are also protected. Automatically learned capitalised vocabulary is useful for prediction but is not automatically trusted as a spelling exception, since it may itself contain typos.

Disable **Automatically correct completed words** independently of predictions if desired. `correction_delay_ms` and `correction_timeout_ms` are configurable in the JSON file. Both requests run independently through dedicated model servers, but they still share your hardware resources.

The bundled **qwen2.5:3b uses the Qwen RESEARCH LICENSE AGREEMENT**, permitting non-commercial research/evaluation; commercial use requires a separate licence. qwen3.5:9b uses Apache 2.0. Original model agreements and required attribution notices accompany the release.

## Local data

Configuration, `memory.db` and error logs live in `%LOCALAPPDATA%\NPPAI`. Accepted suggestions and recent context are stored locally when learning is enabled. Memory retrieval uses trusted phrases, heuristic style and relevant dictionary entries. Phrases need at least 20 acceptances before promotion; a replacement phrase starts its trust count again. Edits inside an accepted completion are marked and excluded from style inference. Disabling learning stops new observations; disabling memory stops retrieval. Existing data is retained.

The local API credentials are generated separately for each model process and are never saved in configuration. There is no cloud service. Existing personal settings and memory stay in the same location; obsolete Ollama endpoint/startup fields are ignored. The packaged model IDs retain the previous defaults, so existing model selections continue to work.

## Build and verify

For development, `.\scripts\Start-Local.ps1` builds, verifies and installs into the separate `.local\Notepad++` copy. Building requires PowerShell 7, the .NET 8 SDK, Visual Studio C++ Build Tools and Windows SDK. The installer script also uses PowerShell 7; copying the complete plugin folder manually needs no PowerShell.

The complete build copies the pinned compatible llama.cpp runner and dependencies from `LlamaSourceDirectory` (the local Ollama runtime by default), and reuses existing local model blobs. A build machine needs that runner distribution; the finished plugin does not need Ollama installed. Runtime files and model copies are checked against pinned SHA-256 values. Missing model blobs can be fetched at build time. Normal plugin use is offline. `Build.ps1 -CodeOnly` skips runtime/model asset preparation and is for CI checks, not distribution.

```powershell
.\scripts\Build.ps1
dotnet run --project tests\NPPAI.Smoke -c Release
dotnet run --project tests\NPPAI.Smoke -c Release -- --lifecycle
dotnet run --project tests\NPPAI.Smoke -c Release -- --runtime-lifecycle
.\scripts\Test-NativePackage.ps1
dotnet run --project tests\NPPAI.Evaluation -c Release -- artifacts/prediction-evaluation.json
```

The smoke harness uses native editor-message fixtures, controlled delayed provider responses, and real requests to both models. It checks independent edits, split-word repairs, range movement and invalidation, in-flight races, UTF-8, predictions, cancellation, undo, trust and persistence. Test learning data is isolated in temporary storage. The evaluation runner compares two models and the old/new prediction configurations; see [the local evaluation report](docs/prediction-evaluation.md) for results and limitations.

`--lifecycle` runs deterministic protocol/editor/focus/timeout checks without loading a model. GitHub Actions runs this mode, builds a code-only x64 package, and uses a native probe to confirm that NPPAI loads its bundled .NET runtime with global runtime lookup disabled. Full smoke and evaluation runs use the complete package in `artifacts\NPPAI` and start their own llama.cpp servers. Historical evaluation files describe the earlier Ollama backend and are not llama.cpp performance results.

Local verification on 8 November 2026 passed all 94 smoke checks with the unchanged `qwen2.5:3b` and `qwen3.5:9b` model copies. Vulkan startup on an RX 7800 XT, CPU fallback, shared model loading, cancellation and owned-process shutdown also passed. A native host confirmed that NPPAI loads its bundled .NET runtime with global runtime lookup disabled. After installation, the reopened Notepad++ loaded the packaged runtime and both owned model servers reported healthy on loopback addresses. Verification output is saved in `artifacts/llama-*-verification.*`.

The manual **Full self-contained release package** GitHub Actions workflow builds an exact application commit, downloads the pinned runtime distribution, and streams the unchanged models into release parts below GitHub's asset-size limit. It verifies every archived file and publishes checksums to an existing draft release; publication follows verification of the uploaded assets. `tests/NPPAI.Release/Test-SegmentedArchive.ps1` checks ZIP header backpatching and reads across part boundaries.

The package is in `artifacts\NPPAI`. To install elsewhere, close that x64 Notepad++ instance and copy **all package files** into its `plugins\NPPAI` folder. The native `NPPAI.dll`, managed DLL, .NET runtime, `runtime`, `models` and `licenses` folders must stay together. Program Files installation may require an administrator.

## Remaining limits

Latency depends on hardware, model size and cold loading; the plan's 300 ms target is not guaranteed. Suggestions stream as text arrives. The overlay uses Consolas and a single line, clipped to the editor edge. Visual verification across DPI, themes and split views remains needed. Adaptation over several days requires real usage. Broader grammar rewrites, SOC templates and document RAG are phase-two features.

Runtime/model provenance is pinned in `scripts/runtime-assets.json` and copied into the package. Upstream llama.cpp, model licence texts and runtime notices accompany the package. Replacing or adding models requires updating this manifest, repackaging and validating model compatibility; the plugin does not download model code or weights at runtime.

The bundled llama.cpp runner is the Ollama-distributed build with upstream compatibility hooks for existing Ollama GGUF exports. Those hooks translate legacy metadata and tensor names in memory while loading. Both model files are copied byte-for-byte and remain unchanged, including their original hashes. This package uses the direct llama-server API, not the Ollama service or API. Runtime provenance and every copied native dependency hash are recorded in `scripts/runtime-assets.json`.
