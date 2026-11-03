# Phase E — AI Copilot (Local-First)

Goal: schema-aware natural language → SQL, "explain this query/plan", and fix-the-error help — local
model first (Ollama), cloud providers only behind explicit opt-in and the PII scrubber (CLAUDE.md §3).

Slices: **E1** provider abstraction + settings → **E2** prompt context + scrubber → **E3** editor
features (Ctrl+I, explain, fix error) → **E4** plan "Explain & optimize" → **E5** cloud providers (opt-in).

---

## Ground rules (write these as Security tests first)
1. **Off by default.** No AI request without the user enabling AI in Settings.
2. **Local default.** First provider is Ollama on `127.0.0.1` only. Any other host = "cloud" rules.
3. **No row data by default.** Context = schema names/types + the user's query text after scrubbing.
   Sending result rows needs a per-request checkbox.
4. **Scrub before send.** String/number/date literals → placeholders using the engine lexer (never regex);
   comments removed; names kept (needed for SQL). Cloud: optional name hashing mode.
5. **Output is untrusted.** Never auto-executed. Shown as a diff; inserted only on accept; then normal
   classifier + destructive + Prod guards in the sidecar apply.
6. **Secrets in the keyring only** (API keys, `querithm` service). Scrubber covers the key header names in logs.
7. **Prompts and responses are user data:** not logged; optional local AI history in `store.db`, off by default.

## Network decision (§16 row needed)
The sidecar today only talks to databases and the SSH bastion. AI adds outbound HTTP from the
**sidecar** (`HttpClient`, no new package), never from Rust/webview (Tauri keeps no `http`
capability). Ollama: `http://127.0.0.1:11434` only. Cloud: HTTPS only, host allow-list per provider,
TLS validation on, proxy from OS settings. Document in §13 and §16.

## E1 — Provider abstraction

### Contract (`sidecar/Querithm.Contract/Ai.cs`, new)
```
record AiSettings { bool Enabled; AiProviderKind Provider; string Model; string? Endpoint; bool AllowRows; bool CloudOptIn; }
enum AiProviderKind { Ollama, OpenAiCompatible, Anthropic }
record AiRequest { Ulid RequestId; AiTask Task; string? SessionId; string Text; Position? Cursor; bool IncludeRows; }
enum AiTask { NlToSql, Explain, FixError, OptimizePlan }
notification ai.delta { Ulid RequestId; string Chunk; }
notification ai.done  { Ulid RequestId; string? Sql; string? Explanation; string? Error; AiUsage? Usage; }
```
RPC: `ai.settings.get/set`, `ai.models` (list from Ollama `/api/tags`), `ai.run`, `ai.cancel`.

### Sidecar (`sidecar/Querithm.Core/Ai/`, new)
- `IAiProvider.cs` — `IAsyncEnumerable<string> StreamAsync(AiPrompt, CancellationToken)`.
- `OllamaProvider.cs`, later `OpenAiCompatibleProvider.cs`, `AnthropicProvider.cs` (use the latest Claude
  model ids from the Anthropic docs at implementation time).
- Source-generated JSON (`Json.cs` context) for request/response bodies — trimming safe.
- Timeouts (connect 5 s, idle 60 s), cancellation on `ai.cancel`, tab close, sidecar shutdown.

## E2 — Context builder and scrubber
- `SchemaContextBuilder.cs` — picks relevant objects from the snapshot: names in the query (tokens),
  FK neighbours (1 hop), name similarity to NL words; cap ~6k tokens; emits compact DDL-like text per engine.
- `PromptSanitizer.cs` — per-engine via `IDialectService` tokens: literals → `?`, comments dropped,
  optional identifier hashing (reversible map kept in memory for the response).
- `ResponseExtractor.cs` — take the first fenced `sql` block; reject if empty; run `Classify` to label
  destructive output before showing it.
- Tests: scrubber never leaks a literal (property test with random literals per engine);
  context respects the cap; identifier hashing round-trips.

## E3 — Editor features (`src/ai/`, new)
- `Ctrl+I` inline prompt above the cursor → streamed SQL → Monaco diff view (shared instance's diff
  mode) → Tab accept / Esc reject.
- "Explain selection" → side panel with markdown (rendered with HTML disabled).
- On query error in Messages: "Ask AI to fix" → sends error text + scrubbed query → diff.
- No inline ghost text on every keystroke in v1 (latency/cost; revisit later with a numeric target).
- Vitest: accept/reject flows, cancel mid-stream, no re-render per chunk (append to a ref, throttle paint 50 ms).

## E4 — Plan "Explain & optimize"
- Serialize `PlanTree` compactly (operators, est/actual rows, costs, warnings, missing-index hints).
- Output: cause, index suggestion (as a script into a new tab, never executed), rewritten query (diff).
- Tests: the plan fixture of every engine active when E ships produces a bounded prompt size; each later
  Phase D engine adds its plan fixture and scrubber property test.

## E5 — Cloud providers (opt-in)
- Settings shows exactly what is sent (preview of the scrubbed prompt) before the first cloud request.
- API key stored in keyring via Rust command; sidecar receives it per request in memory only (same path
  as DB secrets).
- Security tests: cloud call refused without `CloudOptIn`; key never in logs/crash dumps; non-allow-listed
  host refused.

## Failure paths
- Ollama not running → "Start Ollama or pick another provider" (no stack trace). Model missing → list models.
- Stream cut mid-way → partial text shown as partial, nothing inserted.
- Rate limit / 401 → clear message, key re-entry for 401.

## Performance
- §14 row **35** (next free number; 29–34 are taken): "AI context build for the fixture schema — `SchemaContextBuilder` ≤ 50 ms;
  first token latency reported, not gated (depends on model)". Opening AI features must not touch cold start (metric 1).

## Phase E done when
- E1–E4 on every active engine with the local provider set, E5 behind opt-in, all Security tests green, release notes explain privacy.
