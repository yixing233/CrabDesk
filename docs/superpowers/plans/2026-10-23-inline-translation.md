# Inline Translation Widget Implementation Plan

> **For agentic workers:** Execute inline in the current feature branch. Preserve user data and do not commit or push.

**Goal:** Enter text and translate inside the desktop translation card, using the existing AI profile.

**Architecture:** The desktop canvas draws source/output fields, language selection and action buttons. A native WPF TextBox hosted in a small activation-capable WinForms window occupies only the source field while typing, following the existing desktop title editor pattern. Text autosaves through runtime methods; explicit Translate or Ctrl+Enter requests the existing translation service. Clicking elsewhere dismisses input without losing content.

**Tech Stack:** C#, WinForms/WPF ElementHost, existing GDI desktop composition and AI service.

- [x] Add `DesktopWidgetTranslationInput.cs`: multiline IME-capable source field, 12,000-character limit, Ctrl+Enter, Escape/deactivation, opaque colors and DIP layout.
- [x] Add `DesktopBoxForm.WidgetTranslation.cs`: shared hit rectangles, source editing, language menu, direct Translate/Cancel/Copy, input cleanup on widget removal/movement, pause and disposal.
- [x] Update widget rendering/input partials to show the source/output interface and route clicks locally. Settings retains instance configuration and provides instructions for desktop use.
- [x] Add runtime source/language update methods so stale requests are canceled and previous responses cannot overwrite edited text.
- [x] Verify native source input/focus, Ctrl+Enter, actual language menu selection, no settings navigation, loading/cancellation, stale responses, save/reload and rendered bounds with isolated test data. Source input and button/shortcut translation use a mock HTTP handler. Copy uses the existing STA clipboard pattern; clipboard ownership was not changed during automated validation.
- [x] Build/test, update widget documentation and gracefully run the verified new build. Core 502 and runtime/WinUI 737 tests pass; native gallery/translation harness and live Xiaomi search/geo/forecast pass. Final app build has 0 errors; existing warnings remain. Running `artifacts/widgets-inline-build/CrabDesk.WinUI.exe`, PID 34048.
