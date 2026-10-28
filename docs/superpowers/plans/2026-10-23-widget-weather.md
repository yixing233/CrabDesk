# Widget Weather Implementation Plan

**Goal:** Add Windows system location and multiple saved cities to each weather widget. Investigate the Xiaomi endpoint requested by the user; switch providers only after verifying a usable interface.

**Architecture:** Each saved city owns its location and cached weather. The active city supplies the existing rendering properties, preserving existing single-city JSON. Runtime cancellation prevents an old city's response from updating a newly selected city. Explicit foreground location requests use Windows Geolocator and retain manual city selection when access is denied.

- [x] Probe Xiaomi endpoints: v2 returns a migration notice; v3 requires appKey/sign. Ask for a usable HTTP endpoint or documentation while continuing independent work.
- [x] Add saved-city state, active selection, legacy migration and separate weather caches.
- [x] Add saved-city selection/removal and explicit Windows location in the editor.
- [x] Add desktop arrows and context-menu city selection; retain visual gallery management.
- [x] Verify persistence, migration, removal, cancellation and native UI interactions; inspect rendered UI. Core 502 tests and runtime/WinUI 737 tests pass; isolated native harness verifies saved-city selection/removal, desktop arrow wrapping and live Xiaomi requests. Location authorization remains a manual device check.
- [x] Build/test, update docs and launch the verified version (`artifacts/widgets-inline-build`, PID 34048), including Xiaomi integration and inline translation.
- [x] Integrate Xiaomi using the CrabTab implementation supplied by the user. Verified live city search, coordinate reverse lookup and current/daily forecast. Preserve provider labels on old cached snapshots; current service uses Xiaomi for all three operations.
