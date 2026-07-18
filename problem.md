# CrabDesk Deep Logic and Stability Review

## Scope

This review was updated on 2026-07-14. It covers runtime lifetime, async event
handling, state persistence, backup/restore, organization rules, Explorer
integration, desktop input, mapped folders, drag/drop, and file operations.

The Release build and the existing automated tests can pass while the problems
below remain. Most high-risk paths are driven by WPF/WinForms events, Explorer
cross-process messages, filesystem timing, or malformed/restored state and are
not exercised by the current unit tests.

## Resolved in the Current Working Tree

### P0: Pausing takeover or exiting could crash during surface disposal

`DesktopSurfaceManager.Dispose` called `Close()` and then `Dispose()` on each
WinForms surface. `Close()` already disposes the form, so the second disposal
called `Cancel()` on an already disposed `CancellationTokenSource`.

Evidence:

- Windows event 1026 recorded `ObjectDisposedException` from
  `DesktopBoxForm.Dispose` during both pause and exit.
- The pre-fix process exited with `-532462766`.

Resolution applied:

- `CrabDesk.Runtime/DesktopSurfaceManager.cs:62` no longer disposes after `Close()`.
- `CrabDesk.Runtime/DesktopBoxForm.cs:184` now makes owned-resource cleanup
  idempotent.

### P1: Surface input used unstable transparent-style switching

The surface toggled `WS_EX_TRANSPARENT` every 50 ms based on the pointer
position. Hardware validation showed the style could remain enabled over box
content, leaving a visible box click-through and repeatedly touching the input
path.

Resolution applied:

- The timer-driven style switching was removed.
- Surfaces start with an empty region and expose only real box/icon regions.
- `WM_NCHITTEST` remains the fail-open fallback outside interactive geometry.

### P1: Startup and recovery could wait forever on Explorer

Startup captures desktop icon positions before showing the settings window.
The capture, move, restore, and hit-test paths used cross-process `SendMessage`
without a timeout. A busy, restarting, or hung Explorer could therefore leave
the application in a permanent not-responding state before startup completed.
The recovery guard used the same calls and deleted its marker even when fewer
positions were restored than requested.

Resolution applied:

- `CrabDesk.Native/DesktopIconPositionService.cs` now uses
  `SendMessageTimeout` with `SMTO_ABORTIFHUNG` and a 500 ms bound for every
  Explorer list-view message.
- A remote list-view session stops issuing messages after its first timeout or
  remote-memory failure.
- Move/restore counts include only list-view operations that report success.
- `CrabDesk.IconGuard/Program.cs` uses the same bounded message policy and
  retains the recovery marker after partial or failed recovery.
- `CrabDesk.Runtime/CrabDeskRuntime.cs` clears in-memory/marker positions only
  after verified complete restoration.

Verification:

- The rebuilt single-file publish stayed responsive throughout a 12-second
  controlled startup, settled to idle, handled the second-instance exit signal,
  and removed the recovery marker after a verified clean exit.

## Open P1 Findings

### 0. Desktop takeover can make native desktop interaction unavailable

The runtime globally hides Explorer icons at
`CrabDesk.Runtime/CrabDeskRuntime.cs:1489`, then the full-monitor WinForms surface
reimplements interaction for every unassigned icon as well as every box. In
the captured failure state, one 1920x1080 surface owned 62 loose-icon regions
plus two boxes, covering approximately 684,256 pixels (33% of the monitor).
Explorer was hidden, so all common desktop interaction depended on the proxy
surface even though it does not provide Explorer's complete input behavior.

Immediate fail-open mitigation initially applied:

- Schema 16 initially forced existing takeover configurations to restart
  paused once. This was removed after the input recovery fix because it also
  made every existing desktop box disappear after upgrade. Migration now
  preserves the user's explicit `TakeOverDesktop` preference.
- Normal shutdown was used to restore Explorer visibility and remove the
  recovery marker before rebuilding.
- A local diagnostic log now records startup, takeover, surface-region counts,
  mouse down/up/double-click events, host failures, autosave failures, and
  unhandled exceptions.

Resolution applied:

- CrabDesk no longer hides Explorer's icon layer when desktop boxes start.
- Unassigned items remain native Explorer icons with native selection, drag,
  keyboard, and context-menu behavior.
- Only assigned items are parked outside the visible desktop while their
  original coordinates are retained for unassign, pause, exit, and IconGuard
  recovery.
- The CrabDesk desktop child window owns only box rectangles, so empty desktop
  input and Explorer's desktop context menu remain native.

### 2. Unhandled async event failures can terminate the process

Several `async void` or async-delegate entry points have no top-level exception
boundary:

- `CrabDesk.Runtime/CrabDeskRuntime.cs:1795` (`OnHostTimer`)
- `CrabDesk.Runtime/CrabDeskRuntime.cs:2070` (`OnSaveTimer`)
- `CrabDesk.Runtime/CrabDeskRuntime.cs:2360` (`OnDesktopItemsChanged`)
- `CrabDesk.Runtime/CrabDeskRuntime.cs:1964` and `:1983` (power callbacks)
- `CrabDesk.Runtime/DesktopBoxForm.cs:1025` (drag/drop)
- `CrabDesk.Runtime/DesktopBoxForm.cs:1289` and `:1350` (delete commands)
- `CrabDesk.WinUI/Views/BackupPage.xaml.cs:90` (backup retention timer)

For example, the normal-box external-drop branch at
`CrabDesk.Runtime/DesktopBoxForm.cs:1102` awaits file import outside the protected
mapped-box branch. Access denied, a missing source, a path-too-long error, or a
failed refresh escapes an `async void` handler and can terminate the app.
Recycle-bin deletion has the same failure mode.

The desktop provider can also throw outside its per-entry catch when
`Directory.EnumerateFileSystemEntries` fails at
`CrabDesk.Native/DesktopItemProvider.cs:52`; the exception then reaches
`OnDesktopItemsChanged`.

Required direction:

- Route every async event through a common `Task` method with a top-level
  catch/log/report boundary.
- Coalesce timer/watcher refreshes and make shutdown cancellation explicit.
- Add failure-injection tests for save, enumeration, import, delete, resume,
  and reconnect.

### 3. Autosave serializes a live mutable state graph

`CrabDesk.Runtime/CrabDeskRuntime.cs:2076` passes the live `State` object to
`CrabDesk.Core/JsonLayoutStore.cs:36`. JSON serialization continues
asynchronously while UI handlers can mutate boxes, rules, lists, dictionaries,
and appearance objects. `JsonLayoutStore.NormalizeState` also mutates that same
graph before serialization.

Consequences include mixed-version snapshots, collection-modified exceptions,
and process termination through the unhandled autosave timer.

Required direction:

- Deep-clone an immutable save snapshot on the UI thread.
- Normalize and serialize only the snapshot.
- Serialize all layout/backup/export operations through one state-snapshot
  boundary rather than only locking file writes.

### 4. Copying a directory into its descendant can recurse until failure

`CrabDesk.Native/FileOperationService.cs:111` accepts arbitrary source and
destination paths, and `CopyDirectory` at `:198` has no ancestor/identity
guard. Copying `C:\foo` into a mapped folder such as `C:\foo\bar` creates
`C:\foo\bar\foo`, then recursively enumerates the newly created destination.
The operation can continue until path limits, disk exhaustion, or cancellation.

Required direction:

- Resolve paths and reject identical destinations and descendants before any
  directory is created.
- Detect reparse points and directory identities to prevent junction/symlink
  cycles.
- Clean up a partially created destination on failure where safe.

### 5. Smart organization can claim and rewrite user-owned rules

`CrabDesk.Runtime/CrabDeskRuntime.cs:1577` identifies built-in rules by localized
display title. A user rule named like a built-in category can be enabled,
retargeted, recolored, moved, or removed by `EnsureSmartOrganizationStructure`.

Required direction:

- Give built-in rules and boxes stable ownership metadata or reserved stable
  IDs.
- Never infer ownership from a display title.
- Preserve user-edited rules and manually positioned boxes.

### 6. Organization undo is not a complete transaction

`CrabDesk.Runtime/CrabDeskRuntime.cs:1105` snapshots only `State.Assignments`.
`UndoLastOrganization` at `:1185` does not restore created/deleted boxes,
rules, priorities, item order, positions, or settings, and it writes previous
target GUIDs without checking that those boxes still exist. Items can become
assigned to a missing box and disappear from every view.

Required direction:

- Snapshot all organization-owned state as one transaction.
- Validate target IDs on restore and restore item order with assignments.
- Keep undo available only while the structural preconditions still hold.

### 7. One incomplete enumeration can permanently erase grouping

`CrabDesk.Runtime/CrabDeskRuntime.cs:1159` immediately deletes every assignment and
item-order entry missing from the current `Items` snapshot. The desktop
provider skips entries on transient I/O/access errors at
`CrabDesk.Native/DesktopItemProvider.cs:61`, so a cloud placeholder, temporary
lock, permission transition, or unstable fallback identity can erase persisted
grouping.

Required direction:

- Do not prune persistence after one missing observation.
- Reconcile by stable identity and path, with tombstones or repeated misses.
- Distinguish a complete enumeration from a degraded one.

### 9. Paused mode can hide Explorer icons and leave a blank desktop

`CrabDesk.Runtime/CrabDeskRuntime.cs:630` removes CrabDesk surfaces when paused but
does not disable the desktop double-click monitor. The callback at `:1935`
does not check `IsPaused` and always hides Explorer icons at `:1940`. A desktop
double-click while paused can therefore remove the native icons while no
CrabDesk surface exists to replace them.

Required direction:

- Disable the monitor while paused, or implement a separate paused-mode action
  that toggles native visibility without assuming a CrabDesk surface.

### 10. The low-level mouse hook performs remote synchronous work

`CrabDesk.Native/DesktopDoubleClickMonitor.cs:48` calls
`DesktopIconPositionService.IsEmptyPoint` inside `WH_MOUSE_LL`. That path opens
Explorer, allocates remote memory, writes process memory, and sends a
synchronous list-view hit-test before returning from the hook.

This can delay system-wide mouse input and lets Windows remove the hook after
`LowLevelHooksTimeout`.

Required direction:

- Return from the hook immediately after recording click metadata.
- Perform desktop hit-testing on a worker/dispatcher path with a timeout.

### 11. Mapped UNC checks can block the UI thread

`CrabDesk.Native/MappedFolderProvider.cs:27` calls `Directory.Exists` while
adding watchers. `CrabDesk.Runtime/CrabDeskRuntime.cs:1752` invokes that method on
the UI continuation. An offline UNC path can block the settings window,
desktop surface, and host timer for a long OS network timeout.

Required direction:

- Move all reachability checks and watcher creation off the UI thread.
- Add bounded timeouts/cancellation and retain the last good snapshot.
- Avoid queueing refresh work after runtime disposal.

## Open P2 Findings

### 12. Monitor normalization misses coordinate-affecting changes

`CrabDesk.Runtime/CrabDeskRuntime.cs:1816` compares only monitor ID and pixel
bounds, so DPI, work-area, primary-monitor, and taskbar-position changes do not
rebuild surfaces.

`CrabDesk.Core/LayoutCoordinator.cs:18` then discards `WorkArea.X/Y` and clamps
against `(0,0,width,height)`. A top or left taskbar can overlap boxes.
`CrabDesk.Core/Models.cs:114` can also call `Math.Clamp` with max below min when
the available work area is smaller than the 220x120 minimum, throwing during a
display transition.

### 13. Hidden system items still render as loose desktop items

`CrabDesk.Runtime/CrabDeskRuntime.cs:616` filters public `Items`, but
`GetUnassignedDesktopItems` at `:287` reads `_allDesktopItems`. With
`ShowSystemItems=false`, unassigned shell items can still be laid out and drawn
by `DesktopBoxForm`.

### 14. Dotted desktop folders have destructive display/rename behavior

`CrabDesk.Native/DesktopItemProvider.cs:69` uses
`Path.GetFileNameWithoutExtension` for directories. `Project.v2` is displayed
as `Project`; accepting that value in rename changes the real folder name and
removes `.v2`.

### 15. Icon positions are matched by ambiguous display names

`CrabDesk.Native/DesktopIconPositionService.cs:96` and
`CrabDesk.Runtime/CrabDeskRuntime.cs:2276` key positions by Explorer display text,
not stable item identity. Files such as `report.txt` and `report.pdf` can both
appear as `report` when extensions are hidden, causing capture/restore to map
both items to one coordinate and overlap them.

### 16. Desktop hosting ignores critical Win32 failures

`CrabDesk.Native/DesktopWindowTools.cs:37` does not validate
`Get/SetWindowLongPtr`, `SetParent`, or later `SetWindowPos` results.
`CrabDesk.Native/DesktopHostService.cs:11` reports availability from the parent
alone and does not require a valid desktop view/list view. A failed attach can
leave a top-level surface or hide Explorer icons without a usable replacement.

### 17. Second-instance commands can be silently lost during startup

The first instance acquires the mutex at `CrabDesk.WinUI/App.xaml.cs:30`, but does
not create command events until `:73`. A second launch in that interval sees
the mutex, fails `OpenExisting` at `:58`, swallows the exception, and exits.
Activate, organize, undo, and exit commands can all be lost.

### 18. Provider shutdown has callback races

`CrabDesk.Native/DesktopItemProvider.cs:112` calls `Timer.Change` without
checking `_disposed`; an already queued watcher callback can race `Dispose` and
touch a disposed timer. Mapped-provider events are subscribed with an anonymous
runtime lambda at `CrabDesk.Runtime/CrabDeskRuntime.cs:79`; queued callbacks can
enter `RefreshMappedFoldersAsync` after provider/semaphore disposal.

### 19. Imported state does not repair duplicate or empty IDs

`CrabDesk.Core/JsonLayoutStore.cs:212` normalizes box fields but does not repair
`Guid.Empty` or duplicate box/rule IDs. `Guid.Empty` is also the loose-item
pseudo-box ID in `CrabDesk.Runtime/DesktopBoxForm.cs:59`. Duplicate IDs make
`First`, assignment, item-order, rule-edit, hit-test, and render behavior
ambiguous; an empty-ID real box becomes partly non-interactive.

### 20. Core and runtime rule application disagree on mapped boxes

`CrabDesk.Core/OrganizationRuleEngine.cs:72` accepts every box as a valid rule
target, while `CrabDesk.Runtime/CrabDeskRuntime.cs:1102` excludes mapped boxes.
Tests of the core engine can pass for behavior that the application rejects.

Conflict detection at `CrabDesk.Core/OrganizationRuleEngine.cs:171` only
recognizes identical patterns or `*`, so overlapping patterns such as
`report*` and `*2026` are not reported.

### 21. Backup/export sidecar files can be overwritten

`CrabDesk.Core/JsonBackupService.cs:113` uses the predictable path
`destination + ".tmp"` with `FileMode.Create`, then unconditionally deletes it.
Exporting `layout.json` can truncate and delete an unrelated existing
`layout.json.tmp` file.

Daily backup is also awaited as a startup prerequisite at
`CrabDesk.Runtime/CrabDeskRuntime.cs:242`. An inaccessible imported backup path can
make the entire app report startup failure instead of starting without the
backup.

### 22. Multi-item file imports are not transactional

`CrabDesk.Native/FileOperationService.cs:122` mutates sources/destinations one
at a time. If a later item fails, earlier items remain copied or moved but the
method throws without returning the completed paths. Callers cannot reconcile
state or accurately clear/update clipboard and assignments.

### 23. Explorer desktop parent can remain disabled after CrabDesk exits

Live inspection after the reported repeated Windows notification sounds found
no CrabDesk process, no recovery marker, visible Explorer icons, and a
responsive Explorer process. However, the active desktop host was
`Progman` with `WS_DISABLED` (`IsWindowEnabled=false`). Its
`SHELLDLL_DefView` and `SysListView32/FolderView` children were present, but a
disabled parent prevents them from receiving input. This exactly explains why
every desktop click produced a sound while the rest of Windows remained alive.

No visible or hidden modal owner window and no stale mouse capture/menu loop
was present, so the disabled state was orphaned rather than an active dialog.
The previous recovery contract restored icon visibility and positions only.
It did not validate or restore the desktop parent's input state.

The fix makes startup, pause, surface teardown, and normal disposal verify the
current Explorer desktop parent with `IsWindowEnabled` and repair it with
`EnableWindow(..., true)` when necessary. `CrabDesk.IconGuard` performs the
same repair after forced termination, and the desktop verifier now checks the
state during takeover, after clean exit, and after forced-exit recovery.

### 24. Desktop context-menu root is visible but has no executable command

The registered `DesktopBackground\Shell\CrabDesk` key used an empty
`SubCommands` value and nested `shell` keys. The visible root item had no
`command` of its own. On the affected Explorer build, selecting the visible
CrabDesk item did not launch a process; the application log contained no
second-instance startup or command signal.

Registration now uses one `CrabDesk` root with `ExtendedSubCommandsKey` and a
dedicated per-user command store containing three executable verbs: Open
Settings, Create Box, and Smart Organize. Create Box uses a distinct
`--create-box` single-instance command and resumes desktop takeover when needed
so the new box is immediately visible. This preserves one integrated menu without relying on the
broken empty `SubCommands`/inline-shell layout. Re-registration deletes both
legacy layouts first. The single-instance sender also retries for two seconds
while the first instance creates its named events, logs delivery, and starts
command listeners only after runtime initialization so an early organize
command cannot mutate an uninitialized state.

### 25. Schema 16 migration hides every existing desktop box

The temporary crash-containment migration unconditionally assigned
`TakeOverDesktop=false` for every state older than schema 16. The box models
remained in JSON, but runtime startup intentionally created no desktop surface,
so the result looked like a rendering regression and affected healthy layouts
as well as problematic ones.

The forced assignment has been removed. Existing takeover preferences are now
preserved, while startup/guard recovery handles the actual Explorer input
failure. The affected local state was explicitly restored to takeover mode.

### 26. WinForms restores `WS_CLIPSIBLINGS` and clips the entire surface

The live surface reported a valid parent, region, paint pass, and top Z-order,
but its native style was `0x56000000`. The `WS_CLIPSIBLINGS` bit remained set,
so the full-screen `SHELLDLL_DefView` sibling clipped the CrabDesk child window.
Explorer icons were already hidden, producing a completely blank desktop even
though runtime diagnostics claimed one active surface.

The surface now removes `WS_CLIPSIBLINGS` in `CreateParams` and again after
native parenting/positioning, applies `SWP_FRAMECHANGED`, and reads the style
back before takeover. Explorer icons are hidden only after the surface has
painted, is enabled, has no transparent/clipping style, and is confirmed above
`SHELLDLL_DefView`. Any failure rolls the runtime back to paused mode and
restores native Explorer icon visibility. The verified live style is now
`0x52000000`, with the CrabDesk surface at Z-order 0 and DefView at Z-order 1.

### 27. Proxying every unassigned icon requires preserving native coordinates

The first proxy implementation placed more than 60 unassigned Explorer items
into a synthetic fixed grid. Labels overlapped, icons appeared crowded, and the
result no longer matched the user's desktop layout.

The proxy layer now uses each icon's captured Explorer coordinates rather than
inventing a new grid. Its window region is the union of actual proxy item cells
and boxes, not a full-monitor rectangle, so blank-area clicks continue to reach
Explorer. Original positions remain captured for pause, unassign, recovery,
and exit.

### 28. Box dragging performs desktop-wide work for every mouse event

The box move and resize paths called `CrabDeskRuntime.BoxChanged` for every
`MouseMove`. Each call rebuilt the Win32 interaction region, refreshed desktop
surfaces, raised settings-window change notifications, restarted the autosave
timer, and synchronously moved assigned Explorer list-view items through remote
process memory. The surface also painted without optimized double buffering.
That combined UI-thread work explains both the visible flashing and the input
lag while a box was being dragged.

Interactive move and resize now update only the in-memory bounds, the precise
box interaction region, and the local paint invalidation. Workspace
notifications, persistence scheduling, full surface refresh, and Explorer icon
synchronization are committed once after mouse release. The surface uses
WinForms optimized double buffering, and duplicate geometry rebuilding was
removed from the region update path. Resize follows the same final-commit path
as move, so the persisted model and native icon placement remain consistent.

### 29. A full-screen drag region hides every native Explorer icon

The first drag optimization temporarily expanded the CrabDesk surface region to
the monitor-sized client rectangle. Because that surface is deliberately above
Explorer's `SysListView32`, the rectangular child window obscured the native
desktop icon layer for the duration of every move or resize operation. The
icons were not deleted or globally hidden; they were covered by the oversized
CrabDesk region.

The full-screen transform region has been removed. Each pointer update now
rebuilds only the union of the actual box rectangles. Region calculation no
longer rebuilds item geometry, while the expensive runtime notifications and
Explorer synchronization remain deferred until mouse release. Native icons
outside the moving box therefore remain visible throughout the drag.

### 30. Independent region rounding makes a moving box appear to resize

The desktop surface region was built from scaled `RectangleF` values. Win32
rasterized the floating left and right edges independently, so a subpixel box
position could make the physical region width or height alternate by one pixel
between pointer updates. A pending collapse animation or hover transition could
also continue changing the visual height after the user had started dragging.

Move and resize coordinates are now snapped to the monitor's physical pixel
grid. Region rectangles use one rounded origin plus a stable rounded size, and
the selected box's height animation is completed before a transform begins.
Hover transitions are suspended while move or resize capture is active. The
stored width and height therefore remain fixed during a move, while resize
changes in deterministic one-physical-pixel increments.

### 31. Unhandled child `WM_CONTEXTMENU` reaches the Explorer desktop

The CrabDesk surface is a child of `SHELLDLL_DefView`. Even though right mouse
down opened the CrabDesk box or item menu, the later `WM_CONTEXTMENU` message
still reached the default child-window procedure. That procedure can forward
the unhandled message to its Explorer parent, exposing the native desktop menu
inside a box.

The surface now consumes `WM_CONTEXTMENU` directly. CrabDesk's box and item
menus continue to open explicitly from the right-button handler, while the
Explorer desktop menu remains available only outside CrabDesk box regions.

### 32. Deferred native-icon synchronization exposes duplicate icons during box moves

Assigned applications are rendered by CrabDesk while their original Explorer
list-view icons are parked underneath the box. The drag optimization deferred
all Explorer synchronization until mouse release. As soon as a box moved away
from its previous rectangle, those parked native icons became visible at the
old position, creating a detached duplicate behind the moving box.

The intermediate per-drag native move session still allowed Explorer repaint
and coordinate behavior to leak through the composited surface. Active desktop
takeover now captures all original icon positions, hides the Explorer icon
layer, and renders both assigned and unassigned items as CrabDesk proxies.
Loose proxies retain their captured desktop coordinates and receive only small
per-item interaction regions, so blank desktop input continues to pass through
to Explorer. Pause, clean exit, failed startup, and recovery restore the
previous Explorer visibility state and every captured native icon position.

### 33. Proxy icons lose alpha and loose desktop items cannot be repositioned

`Image.FromHbitmap` discarded the alpha channel returned by
`IShellItemImageFactory`, so transparent icon pixels became opaque black. The
proxy interaction path also treated every loose-item drag as a file drag and
had no desktop-position commit path.

Shell images are now requested as icon-only 32-bit images and copied from the
native DIB into an ARGB bitmap before scaling. Loose proxies use captured mouse
movement, preserve multi-selection offsets, snap the final delta to Explorer's
reported item spacing, update the hidden native list-view positions, recapture
the committed coordinates, and refresh the recovery marker. Dropping loose
items onto a normal box still assigns them to that box.

### 34. Fast proxy dragging repaints the desktop and can lose its startup snapshot

Every loose-item mouse move rebuilt the interaction region for every proxy and
invalidated the complete desktop surface. High-frequency input therefore
queued full paints of all icons and boxes, making the dragged icon lag, flicker,
or appear to vanish. A quick Explorer visibility transition during restart
could also make the first position capture return no entries; takeover then hid
the native layer while there were no proxy coordinates to render.

Loose-item preview updates are now limited to approximately 60 FPS. The region
for stationary proxies is cached for the duration of the drag, only selected
proxy bounds are merged per frame, and paint invalidation is restricted to the
old/new drag area. Painting also clips icon and box traversal to the invalidated
area. Mouse release forces the final frame, commits the snapped native move,
and recaptures all Explorer positions so automatic grid rearrangement stays in
sync. Startup retries a transient empty position capture and refuses to hide
Explorer icons if no usable proxy snapshot exists.

### 35. Clicking empty desktop does not clear proxy selection

Blank desktop coordinates deliberately pass through the small CrabDesk window
regions to Explorer. Consequently the proxy surface never received the mouse
down needed to clear its local selection, and expanding the surface to capture
that click would once again cover or block the native desktop.

The existing non-blocking desktop mouse monitor now reports both empty-area
single clicks and double clicks. A single click clears selection on every
desktop surface through the runtime manager, while the optional double-click
visibility action remains independently controlled by its setting. Clicks on
boxes or proxy icons are excluded by window ownership and continue through the
normal CrabDesk input path.

### 36. Application-name font settings do not affect loose desktop proxies

The appearance page already persisted a label font size for every box, but the
unassigned desktop proxy used a separate hard-coded appearance object. Changing
the value therefore updated names inside boxes while ordinary desktop
application names stayed at nine points, making the setting appear incomplete.

Loose proxies now synchronize their label font family, size, visibility, and
shortcut-badge preference from the shared box appearance before every workspace
refresh. The ambiguous label-size slider was replaced by an accessible
"Application name size" number input with half-point steps, so values from 8 to
16 points can be entered precisely and are reflected immediately across all
box and desktop proxy labels.

### 37. Proxy desktop icons do not follow Explorer Ctrl-wheel zoom

The proxy surface used a fixed 88-by-96 interaction cell and a separate icon
size, while Explorer continued to own the hidden desktop grid. Ctrl-wheel could
therefore change the native view without changing proxy rendering or box item
layout, leaving stale coordinates and overlapping hit regions.

The desktop mouse monitor now recognizes Ctrl-wheel only over Explorer or a
CrabDesk desktop child surface. Wheel input over a proxy is forwarded to the
hidden Explorer list view; input over blank desktop continues there normally.
After a short debounce, CrabDesk reads Explorer's persisted icon size and item
spacing, recaptures every native grid position, applies the size to the shared
box appearance, and rebuilds all grid/list layouts. Loose proxy cells derive
their dimensions from the native spacing and current label metrics, so icon
regions, labels, selection, and drag hit testing resize together. Captured
positions are additionally normalized to the dominant Explorer grid origin;
each icon is snapped to its nearest cell and rounded collisions are moved to the
nearest free cell. The same normalization runs when takeover starts, while
subsequent proxy drags preserve the aligned spacing.

### 38. Hover auto-expand is buried in the settings window

Auto-expand is a desktop interaction mode, but changing it required opening the
settings window and navigating to the general page. That separated the control
from the collapsed box where its effect is used and made the current state hard
to discover while working on the desktop.

Every box title bar now contains a Fluent touch-pointer toggle before the
collapse and menu controls. Its accent background indicates that hover
auto-expand is active, hover feedback uses the hand cursor and an explicit
tooltip, and clicking any instance updates the shared setting immediately. The
button is excluded from title dragging, title editing, and the hover-expansion
target itself. Title text and inline editing reserve the additional button
width. The duplicate toggle was removed from the general settings page. When
the mode is enabled from an already open box, all boxes are converted to a
collapsed base state; the source box is adopted by the hover controller and
kept visually open while the pointer remains inside. Leaving it then follows
the normal delayed collapse path instead of leaving the originally open box
expanded indefinitely.

### 39. Entering the auto-expand title button stalls pointer movement

The first mouse move into the new title-bar toggle called `ToolTip.Show`
synchronously. WinForms created and positioned a native tooltip window inside
the mouse-move handler, while the same hover repaint also constructed a new
Fluent icon font. That work produced a visible one-time hitch at the exact
moment the pointer entered the button.

The button now only updates the standard tooltip association from the hot path;
WinForms displays it after the configured 450 ms hover delay. The Fluent icon
font is created once with the desktop surface and reused by every paint, then
disposed with the surface. Entering or leaving the button is reduced to two
small invalidations, a cursor update, and lightweight state changes.

### 40. Hover expansion makes unrelated desktop icons repaint

The height animation rebuilt the managed WinForms `Region` every 15 ms and
called `Invalidate()` on the monitor-sized desktop surface. Although no icon
data refresh or native parking operation occurred, each animation frame asked
Windows to redraw the complete shaped child window, making unrelated desktop
and box icons visibly flash once during expansion.

Surface shape updates now use `SetWindowRgn` through the native region helper
with automatic full-window redraw disabled. Expansion, collapse, animation
ticks, and hover cleanup invalidate only the affected box's maximum visual
rectangle; item hover remains a small per-item invalidation. Explorer data and
assigned-icon parking are untouched by the hover path, and other boxes no
longer participate in its paint cycle.

### 41. Checked menu glyph is vertically offset

Menu rows are normalized to 32 pixels, but `OnRenderItemCheck` used WinForms'
preferred-size `ImageRectangle` for both axes. Its vertical position retained
the pre-normalization layout, so the custom checkmark sat visibly above the
text and selection-row center.

The renderer still uses the check-margin image rectangle for the horizontal
slot, but always derives the vertical center from the menu item's actual
height. Checked items now share the same row center as text and submenu arrows
at every menu width and DPI scale.

### 42. Item-label toggle overlaps the view selector

The icon-and-label settings grid declared rows 0 through 3, while the view and
sort selectors were assigned to row 4. WinUI consequently arranged the row-4
controls in the final available row, causing the view selector to overlap the
item-label toggle.

The grid now declares the missing fifth row. The item-label toggle remains in
row 3, and the view and sort selectors occupy their own row 4 with the shared
section row spacing.

### 43. Hover auto-expand state was shared by every box

The title-bar toggle previously changed `DesktopBehavior.ExpandBoxOnHover`, so
turning it on for one box silently enabled hover expansion for every box and
turning it off disabled them all. The hover controller and tooltip also read
that global flag, so the visual state could not represent an individual box.

Each `DesktopBox` now persists its own `ExpandOnHover` value. Hit testing,
tooltips, timer activation, rendering, and expansion candidates all read the
current box value. Toggling a title button changes only that box; the old
global setting is migrated once to all existing boxes so prior configurations
keep their behavior.

### 44. Acrylic backdrop reverted to Mica after restart

The settings page applied the selected backdrop only to the current window,
while `MainWindow` unconditionally applied Mica in its constructor. No window
material value was stored in `AppSettings`, so every new process discarded the
selection.

The selected material is now persisted as `WindowBackdrop`, normalized to the
three supported values, loaded before the window is created, and saved whenever
the Appearance page changes it.

### 45. Organization card had uneven vertical whitespace

The automatic-organization card overrode the shared section-card padding with
`Padding="16,0"`. Its top content therefore had the expected inset while the
last row sat too close to the bottom border. The card now uses the design
system's symmetric 16px padding on all four sides.

The first correction made the top inset too large because the first setting row
already provides its own vertical height. The final layout keeps the original
zero top inset and adds only a 16px bottom inset, preserving the compact top
alignment while balancing the last row against the card border.

### 46. Backup page could not scroll vertically

The backup page root was a constrained Grid without a page-level scroll
container. On a short window, the records area consumed the remaining row and
content below the viewport was clipped instead of participating in vertical
scrolling. The page now uses an outer vertical ScrollViewer so the settings,
backup records, empty state, and status content share one continuous scroll
surface.

### 47. Page headers duplicated navigation context

Every settings page rendered a large page title and descriptive subtitle even
though the NavigationView already identified the current page. This consumed
vertical space and reduced the useful content area on compact windows. The
page-level title and subtitle blocks are now removed from all views; functional
section titles inside each page remain in place.

### 48. General settings had two incomplete behavior paths

The delete-confirmation switch was honored by the desktop context menu but the
Boxes settings page always showed its own confirmation dialog, even after the
switch was turned off. The setting now gates both deletion entry points.

The interface-animation switch controlled box-height and context-menu
animations, but page navigation still used the system animation setting alone.
Navigation transitions now also respect the CrabDesk animation preference.

### 49. Release page was disabled after GitHub API rate limiting

The About page enabled the release-page button only when the latest API result
contained `ReleasePageUrl`. GitHub rate-limit responses contain no release URL,
so the button became gray even though the repository's Releases page remained
directly reachable.

The runtime now supplies the configured repository's fixed GitHub Releases URL
when cached or API data has no URL. API rate limiting still reports its status,
but no longer disables access to the release page.

### 50. Repeated startup checks kept showing GitHub rate limits

Unauthenticated GitHub API requests share a public per-IP quota. CrabDesk was
checking on every startup and allowed another manual request immediately after
a rate-limit response, so repeated restarts could keep the status at the limit
without giving the quota time to recover.

Update checks now use a six-hour startup cache window, and a rate-limit result
uses a one-hour retry backoff for both automatic and manual checks. The status
also includes GitHub's reset time when the response provides it.

## Missing Regression Coverage

- Repeated close/dispose, pause, host reconnect, topology rebuild, and exit.
- Explorer-hung timeouts for every capture/move/restore/hit-test operation.
- Exceptions from autosave, watcher refresh, drag/drop, delete, resume, and
  reconnect must not terminate the process.
- Concurrent state mutation during save, backup, export, and restore.
- Directory copy/move to self, descendant, junction cycle, and partial failure.
- Smart organization ownership and complete transactional undo.
- Degraded enumeration must not prune persisted assignments.
- Recovery marker retention after partial/failed icon restoration.
- Paused desktop double-click must never hide the only visible icon layer.
- Offline UNC watcher setup must not block the UI thread.
- DPI-only, work-area-only, and taskbar-origin monitor changes.
- Dotted folders, duplicate display stems, duplicate/empty GUIDs, and malformed
  imported state.
- Second-instance commands during every first-instance startup phase.
