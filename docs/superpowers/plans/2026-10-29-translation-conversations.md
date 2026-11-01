# Translation Conversation Reuse Implementation Plan

> **For agentic workers:** Implement the following tasks inline in the current authorized session. Preserve the existing working tree and do not commit or push.

**Goal:** Reuse translation context between requests and let the user configure when an idle conversation expires.

**Architecture:** Keep successful user/assistant pairs in `WidgetTranslationService` memory. Build compatible Chat Completions requests with a stable system message, completed history and the new source. A profile change, idle expiry, or history limit starts a fresh conversation; cancellation, failure and superseded requests cannot append history. Retain at most 32 completed pairs and 24,000 history/current-source characters to bound context growth. HTTP connections continue to use the existing long-lived HttpClient. Do not add provider-specific cache/session parameters or promise a latency improvement.

**Tech Stack:** .NET 8, System.Net.Http, TimeProvider, WinUI 3 ToggleSwitch and NumberBox, xUnit.

### Task 1: Settings and service

**Files:** `CrabDesk.Core/WidgetTranslationSettings.cs`, `WidgetTranslationService.cs`, `JsonLayoutStore.cs`; new `CrabDesk.Tests/WidgetTranslationConversationTests.cs`.

- [x] Add persisted `ReuseConversation` (true) and `ConversationIdleMinutes` (30), normalized to 1–1440 minutes.
- [x] Inject an optional TimeProvider for deterministic expiry tests. Protect mutable session state with a short lock; hold no locks during HTTP requests. Snapshot endpoint, key, model, effective prompt, target language and settings before sending.
- [x] Add `ResetConversation()`. Compare request generation before committing successful responses. Store only complete pairs; canceled, failed and superseded responses leave the completed history unchanged.
- [x] Test request message ordering, completion-time idle reset, timeout boundary, profile/language changes, disabled reuse, explicit reset, cancellation/failure/late completion, context limits and persistence. Use stub HTTP handlers and a manual TimeProvider; no live credentials or network required.

### Task 2: Runtime and settings interface

**Files:** `CrabDesk.Runtime/CrabDeskRuntime.Widgets.cs`, `CrabDesk.WinUI/Views/WidgetsPage.xaml` and `.xaml.cs`; existing runtime tests.

- [x] Extend `ConfigureWidgetTranslation` with optional reuse/idle parameters so existing callers preserve preferences. Reset context and cancel pending translation after profile or session-setting changes. Reset after removing the translation widget or changing its target language.
- [x] Add a native ToggleSwitch for reuse and a NumberBox for idle minutes. Disable time input when reuse is off; explain idle expiry, in-memory lifetime and automatic reset for long conversations. Save through the existing runtime configuration flow without adding a page or content editor.
- [x] Load values under `_loading`; ignore temporary NaN values during numeric editing. Keep layout consistent with existing editor spacing.
- [x] Extend runtime regression coverage for preserving/changing session settings and canceling a pending request.

### Task 3: Validate and run

- [x] Run focused Core conversation/widget tests, then build WinUI and run DesktopWidgetRuntimeTests with a fresh output directory.
- [x] Inspect the real settings controls, verify toggle enablement, change/save/reload timeout and restore the user's original preferences.
- [x] Start the new build and verify desktop takeover remains connected. Record test results and explain that cache-dependent performance is not guaranteed.

**Validation:** 65 Core tests and 64 runtime tests passed. Native UI Automation verified saving/reopening disabled reuse with a seven-minute timeout, disabled time input, and restoration to enabled/30 minutes. Build: `artifacts/translation-conversation-build`. Evidence: `artifacts/verification/translation-conversation-*.log` and `translation-conversation-settings.png`.
