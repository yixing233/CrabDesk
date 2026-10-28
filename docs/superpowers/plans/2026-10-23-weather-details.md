# Weather Details Implementation Plan

**Goal:** Surface Xiaomi's richer weather data in a compact desktop summary and an expandable weather panel.

**Architecture:** Extend each cached snapshot with nullable observations, hourly/daily forecasts, alerts and indices. Parse optional fields without inventing values. A native WinUI weather panel opens from the desktop card independently of settings, with saved-city selection, refresh, metrics, temperature/AQI charts and daily forecasts. Preserve existing snapshot JSON and compact desktop sizes.

- [x] Add optional weather observations and forecast records in Core; parse verified Xiaomi JSON and request 7-day forecast.
- [x] Add runtime weather-details event and wire the desktop weather card and right-click entry.
- [x] Add a themed WinUI weather panel using existing card appearance/acrylic, with hourly charts, daily trends, alerts and indices.
- [x] Verify old/minimal JSON, rich snapshots, cancellation/cache persistence and direct weather navigation; render the panel against live public-city weather using isolated state. Core 503 tests and runtime/WinUI 746 tests pass. Native panel validates live 23-hour/7-day data, actual refresh button, saved-city switching, repeated updates, scrolling and light/dark rendering.
- [x] Build/test, update widget documentation and run the verified version (`artifacts/widgets-weather-details-build`). Final targeted runtime checks: 20 passed; existing compiler warnings remain. No commit or push.
