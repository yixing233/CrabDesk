# Acrylic foreground and hover recovery

Completed in `codex/real-background-acrylic` on 2026-09-10.

## Cause and implementation

`DesktopBoxForm` could submit its first layered bitmap during handle creation, before `SetParent` attached it to the acrylic host. The submitted DIB contained the correct title and content and `UpdateLayeredWindow` returned success, but the child foreground remained invisible. An isolated experiment recreated the layered surface after attachment and immediately restored its pixels.

`AttachToDesktop` now gates presentation until native desktop attachment completes. Both acrylic foreground and shared icon-layer input use this startup sequence. A regression test reproduced premature presentation before the fix and now verifies deferred presentation, successful rendering after attachment, and the native region.

The independent full-monitor child also retained native mouse-leave tracking when the pointer moved into a transparent part of its client rectangle. Its hover controller consequently never received the exit needed to start collapse. While a standalone box is hover-expanded, the existing hover timer reconciles the physical pointer every 50 ms. Normal collapse deadlines remain in effect, and the timer stops after collapse. Shared composition retains its existing deadline scheduling.

## Validation

- Release solution build: 0 errors, 19 existing `MVVMTK0045` warnings.
- WinUI tests: 502 passed.
- Core/Native tests: 297 passed.
- Bootstrapper tests: 39 passed.
- Manager creation, visibility changes, refresh and disposal: three cycles passed.
- Wallpaper Engine: sharp foreground, animated backdrop geometry, physical pointer expansion and exit collapse passed. Hover timer confirmed stopped after collapse.
- Two Show Desktop transitions: passed; foreground and input classification retained.
- `git diff --check`: passed.

The visual probe now uses the production standalone box path and includes a Windows compatibility manifest for layered child windows. Run instructions are in `tools/AcrylicSmokeTest/README.md`.

Pointer validation evidence: `%TEMP%/CrabDesk-AcrylicSmokeTest/20260910-110114/frame-8.png` (expanded) and `frame-10.png` (collapsed). Desktop-transition evidence: `%TEMP%/CrabDesk-AcrylicSmokeTest/20260910-110152/frame-10.png`.

Mixed-DPI multi-monitor dragging, full file-operation flows and long-duration performance were not retested in this repair. The desktop application running from `E:/Code/CrabDesk/artifacts/publish/win-x64` was left running; it has not been replaced with this isolated build. No commit or push was performed.
