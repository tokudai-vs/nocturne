# v3.0.0 Pre-Release Audit Log

## Summary

- **Total rounds**: 1
- **Total must-fix findings (CRITICAL + HIGH + MEDIUM)**: 0
- **Final state**: clean — zero must-fix findings; audit loop exited after round 1
- **LOW / INFO findings noted but not actioned**: 5 (per ground rule: optional)

The loop terminated after a single round because no must-fix findings surfaced from any of the lanes that could run. Static gates (tsc, lint, check-relative-requires, check-bundling) are all green and the targeted verification commands all pass.

---

## Round 1

### Reviews run

| Lane | Status | Notes |
|---|---|---|
| `/security-review` (built-in) | partial — manual full-scope substitute | Skill failed at startup: `git diff --name-only origin/HEAD...` returned `fatal: ambiguous argument 'origin/HEAD'`. The repo has `origin/main` but no `origin/HEAD` symbolic ref. Per the user's full-scope directive (don't accept diff-scoped runs as a substitute), conducted a manual security pass over the entire `src/**` tree using grep-driven pattern discovery + targeted file reads. |
| `/ultraqa` (OMC skill) | partial — manual QA scan substitute | Skill is a workflow cycle that drives a fix-loop against a failing gate; all static gates are green, so the cycle had no starting state. Conducted a manual code-quality / correctness scan in the skill's spirit. |
| `/verify` (OMC skill) | ran | Ran concrete verification commands against the v3 changes. All passed. |
| `/gemini:adversarial-review` | unavailable | Not in this session's available-skills list. Attempted to fulfill via `gemini` CLI (`/home/tokudai/.nvm/.../gemini` v0.38.2): default model `gemini-3-flash-preview` returned 429 `RESOURCE_EXHAUSTED` 10 times in a row; retry with explicit `-m gemini-2.5-pro` returned the same 429. Skipped per user direction ("skip gemini if not working"). |

### Findings

| Severity | Category | File:Line | Description | Status |
|----------|----------|-----------|-------------|--------|
| LOW | Defense-in-depth | `src/renderer/index.html:8` | CSP includes `'unsafe-inline'` for `style-src`. | Accepted — required for React inline `style={...}` props. The renderer has no innerHTML or dangerouslySetInnerHTML path, and contextIsolation + sandbox + no nodeIntegration prevent escalation. |
| LOW | Defense-in-depth | `src/main/window.ts:21-77` (BrowserWindow) | No `will-navigate` handler on `webContents`; a renderer-initiated full-page navigation away from `self` isn't blocked. | Accepted — the renderer never builds an `href={...}` to user-controlled URLs (audited). External links go through `setWindowOpenHandler` which is wired to `safeOpenExternal` (deny + protocol-validated forward to OS browser). |
| LOW | Defense-in-depth | `src/main/ipc-handlers.ts` (multiple) | ~20 IPC handlers run `isValidUrl` / `isNonEmptyString` / `ALLOWED_*` checks; many others take `itemId` strings and forward them to the DB / Emby client without validation. | Accepted — the trust boundary is at `contextBridge`. The renderer can only invoke methods exposed in `src/preload/index.ts`, which the audit confirmed take primitive values. SQL is parameterized (verified separately). |
| LOW | Resource lifecycle | `src/main/sync-engine.ts:76,158` | `syncEngine.driftTimer` is cleared on re-scheduling but not in the `before-quit` handler. | Accepted — Node clears pending timers on process exit; this is a no-op on quit. |
| LOW | Resource lifecycle | `src/main/trakt-scrobbler.ts:29,229` | `traktScrobbler.drainTimer` same shape as above. | Accepted — same reasoning. |
| INFO | Skill availability | n/a | `/gemini:adversarial-review` not present in available skills; gemini CLI quota-blocked for both `gemini-3-flash-preview` and `gemini-2.5-pro`. | Documented. Loop did not block on the missing lane (per ground rules). |

### Items inspected and cleared

For completeness — these are the must-fix categories audited, with no findings:

- **Electron security config** — `src/main/window.ts:38-44` has `contextIsolation: true`, `nodeIntegration: false`, `sandbox: true`; `setWindowOpenHandler` returns `deny` and forwards via `safeOpenExternal`.
- **SQL injection** — all dynamic SQL interpolation is either: (a) hardcoded table/column literals (DML on known names), (b) sanitized via `sanitizeSort()` against `ALLOWED_SORT_COLUMNS` + `ALLOWED_SORT_ORDERS` allowlists, or (c) `${fields.join(', ')}` driven by `ALLOWED_USER_DATA_COLUMNS` allowlist. User-controlled values bind through `?` placeholders only.
- **XSS** — `grep` for `dangerouslySetInnerHTML` and `.innerHTML =` across `src/renderer` returns zero matches; no `<iframe>` / `<embed>` / `<object>` either.
- **shell.openExternal call sites** — all 4 wrapped: `safeOpenExternal` (protocol check) for window-open / window.ts handler; `isValidUrl` (protocol check) for `trakt:open-verification`; trailer URL composed from regex-extracted YouTube ID `[A-Za-z0-9_-]{11}` so the resulting URL is structurally constrained.
- **Hardcoded secrets / API keys** — none in source.
- **Token storage** — `src/main/trakt-store.ts` uses Electron `safeStorage` (DPAPI on Windows) with an explicit `isEncryptionAvailable` gate; refuses to operate when unavailable.
- **Process spawn (mpv)** — `src/main/mpv-manager.ts:56` uses `spawn(mpvPath, args)` with an explicit argv array (not `shell: true`). No command injection surface.
- **axios timeouts** — `emby-client.ts` 10s, `trakt-client.ts` 15s, `introdb-client.ts` `TIMEOUT_MS`, `image-cache.ts` 30s. All explicit.
- **CSP** — present in `src/renderer/index.html`: `default-src 'self'; script-src 'self'; object-src 'none'; …`.
- **Schema migration ordering** — re-verified via sqlite3 CLI against both a fresh DB and an "old-style" DB without `last_played_date`; both end with column present + partial index created.
- **Stale-queue prune classification** — re-verified via sqlite3 CLI: 5 synthetic rows (mixed valid + poison) reduce to 2 valid rows after the prune logic runs.

### Fixes applied

None — zero must-fix findings.

### Verification

| Gate | Result | Evidence |
|---|---|---|
| `tsc -p tsconfig.node.json` | pass | exit 0, 0 lines |
| `tsc -p tsconfig.web.json` | pass | exit 0, 0 lines |
| `npm run lint` | pass | exit 0 (eslint scripts/ + check-relative-requires) |
| `scripts/check-relative-requires.js` | pass | OK — no dynamic relative require()s in src/ |
| `scripts/check-bundling.js` | pass | OK — 20 src/main/*.ts modules accounted for in out/main/index.js (265 KB) |
| Migration replay (fresh + old DB) | pass | both end col=1, idx=1, query refs col succeed |
| Stale-queue prune classification | pass | 5 rows in, 3 dropped, 2 remain (valid stop/pause in 1-80% progress band) |

### Exit

No must-fix findings → audit loop exits after round 1 per the original prompt's stop condition.

---

## Round 2

Not executed — exit condition met after round 1.

## Round 3+

Not executed — exit condition met after round 1.

---

## Notes on lane limitations

A genuinely thorough adversarial review needs at least one cross-model reviewer or a static-analysis layer that catches what tsc/eslint don't (e.g., a SemGrep/CodeQL ruleset for Electron). The lanes available in this session are:

1. **`/security-review`** — depends on a `git diff origin/HEAD...` baseline; that's a recent-change scope, not a v1/v2 surface scan. The skill itself doesn't have a `--full` mode at v4.10.1. Manual substitute used here is grep-driven, which catches pattern-level smells but misses semantic issues a real reviewer would find.
2. **`/ultraqa`** — workflow cycle, not a finding-producer. Useful when there IS a failing gate, less so when everything's green.
3. **`/verify`** — workflow guidance for "prove this works." Used here as a checklist of concrete verification commands.
4. **`/gemini:adversarial-review`** — would have been the second-opinion lane and is the one that's currently quota-blocked.

**Recommendation for future audits**: when the v3.x cycle adds new features, re-run the gemini lane on a day when Google's preview-model capacity isn't exhausted. The first 3 lanes can be re-run any time; they're cheap.
