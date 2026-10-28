# immeubleCAD — Roadmap

Goal: a desktop application, simple enough for a beginner, to model a building (house or apartment block) from its floor plans and lay out its networks: heating pipes, hot and cold water, drainage. It runs natively on **macOS, Linux and Windows**.

The user flow is: **import JPG plans → walls and levels are built → place radiators and risers (colonnes) → lay pipes → get a 3D view and a bill of materials.**

The application skeleton, conventions and quality bar follow **CinnabarSharp** (https://github.com/pgourlain/CinnabarSharp): same stack, same project split, same "Core is non-visual" rule, same "Actions are the only way to edit a document" rule, same per-OS validation checklists, same MCP server design. When in doubt about how to structure something, look at how CinnabarSharp does it.

## Stack decision

**Avalonia UI 12 on .NET 10 (LTS), MVVM with CommunityToolkit.Mvvm, 3D with Silk.NET (OpenGL) inside an Avalonia `OpenGlControlBase`.**

- Same stack as CinnabarSharp, so the shell, dialogs, menus (native on macOS), settings, packaging and CI can be copied from it.
- 2D plan view: custom Avalonia `Control` (like CinnabarSharp's `CanvasView`), drawing with Avalonia's `DrawingContext`.
- 3D view: `OpenGlControlBase` + `Silk.NET.OpenGL`. Meshes are generated in pure C# (see `ImmeubleCad.Scene`), so the 3D view only uploads buffers and draws. Fall back to a simple software preview in headless tests.
- Wall detection is **copied** from CinnabarSharp (`LineExtraction` from `CinnabarSharp.Core/Photo`, `MaskTracer` from `CinnabarSharp.Vector`) to keep the project autonomous. See Phase 3.

## Architecture rules (also copy them into CLAUDE.md)

- **Units:** every length in the model is an `int` in **millimetres**. No floating point in the model; floats only for rendering. Snapping, equality and tests become exact.
- **Axes:** X east, Y north on the plan, Z up. A level has an altitude (Z of its finished floor) and a height.
- **`ImmeubleCad.Core` is non-visual:** document model, history, tools (with UI-independent pointer input), import, exports. Forbidden: Avalonia, SkiaSharp, Silk.NET, `System.Drawing`, dispatchers, dialogs, P/Invoke. Allowed: BCL, `Microsoft.Extensions.*`, Magick.NET (image decoding only). Enforced by `CoreArchitectureTests`.
- **`ImmeubleCad.Plan` and `ImmeubleCad.Scene` are BCL only** (no Core, no Magick.NET, no UI). Enforced by architecture tests.
- **`BuildingActions` is the only way to edit a building.** Each action changes the model and pushes one `IHistoryItem`. `HistoryTests.Actions` must list every action and checks that undo restores the model exactly.
- **Core talks to the UI through events** (`IBuildingEventsService`, observable, like CinnabarSharp's `IDocumentEventsService`). Tests assert the exact ordered sequence of events.
- **The 2D plan is the main view.** 3D (solid, X-ray, wireframe) is one key away and never loses context. Plan view and 3D view are two separate controls sharing one **`ViewState`** (Core, non-visual): current level, visible levels, selection, look-at point (mm), zoom scale, render mode, cut above current level. Neither view owns this state; switching views only changes which control is shown.
- **Hybrid input, detected automatically:** press and release without moving = **click mode** (AutoCAD style: a rubber band follows the mouse until the next click); press, move more than a few pixels, release = **drag mode** (Paint style: release finishes). Users never pick a mode. Each tool is a state machine in Core (`Idle → Pressed → Dragging | Placing → Committed`) fed with `ToolPointer` in mm, unit-tested without UI. A whole gesture (a chained wall or pipe run included) is **one history step**.
- **Fittings are derived, never drawn:** elbows, tees, reducers and floor crossings are computed from the network graph. The user only manipulates nodes and segments.
- **Logging everywhere through `Microsoft.Extensions.Logging` (`ILogger<T>`)**: Core and the other libraries only use the abstraction; the file sink lives in Desktop (`AppLog`, like CinnabarSharp). Never swallow an exception silently: log it, then show a dialog or a status message. In `--mcp` mode stdout is the protocol, so logs go to the file and stderr only.
- Nullable reference types and implicit usings enabled. User-visible strings go through `Translations` (French and English from the start).
- Releases are Native AOT: no reflection-based `System.Text.Json` (source-generated contexts), no `new Binding(path)` from code. Keep trim/AOT warnings at zero.

## Projects

| Project | Contents |
|---|---|
| `ImmeubleCad.Core` | Building model, history, actions, tools, file format, import pipeline, exports. Non-visual. |
| `ImmeubleCad.Plan` | Copied and adapted wall detection: `LineExtraction` (mask), `MaskTracer` (outlines), plus `WallFitter` (outlines → wall segments). BCL only. |
| `ImmeubleCad.Scene` | Pure C# mesh generation: wall extrusion, slabs, pipe cylinders, fittings, equipment boxes; picking (ray/mesh). BCL only. |
| `ImmeubleCad.Geo` | Phase 12: geocoding, cadastre and map tiles over HTTP (IGN Géoplateforme), disk cache. No UI. |
| `ImmeubleCad.Mcp` | MCP server (official C# SDK `ModelContextProtocol`), Core only. Hosted by the desktop executable with `--mcp`. |
| `ImmeubleCad.Desktop` | Avalonia 12 app (MVVM): plan view, elevation view, 3D view, panels. Executable named `ImmeubleCad`. |
| `ImmeubleCad.Core.Tests` | xunit v3. Model, history, actions, tools, import, exports, `CoreArchitectureTests`. |
| `ImmeubleCad.Plan.Tests` | xunit v3. Detection on synthetic plans, pinned checksums. |
| `ImmeubleCad.Scene.Tests` | xunit v3. Mesh generation, picking, pinned checksums. |
| `ImmeubleCad.Desktop.Tests` | Avalonia.Headless + Skia, xunit v3. Drives the real window, saves screenshots per OS. |
| `ImmeubleCad.Mcp.Tests` | xunit v3. Starts `ImmeubleCad --mcp` and drives it over stdio. |

Root solution: `ImmeubleCad.slnx`.

## How to validate each phase

Every phase ends with a **Validation** checklist. Run it on macOS (Apple Silicon), Linux (Ubuntu 24.04, X11 and Wayland) and Windows 11, and don't start the next phase until it passes on all three. CI builds and runs every test on the three OSes and uploads the headless UI screenshots per OS (`screenshots-<os>`).

---

## Phase 0 — Foundation

- [x] Create `ImmeubleCad.slnx` with all projects of the table above (empty where needed), targeting `net10.0`.
- [x] `Directory.Build.props`: nullable, implicit usings, warnings as errors for nullable, `LangVersion latest`.
- [ ] `CoreArchitectureTests`: fail if Core references Avalonia, SkiaSharp, Silk.NET or `System.Drawing`. `PlanArchitectureTests` and `SceneArchitectureTests`: BCL only.
- [x] DI entry point `ServiceExtensions.AddImmeubleCadServices()` in Core.
- [x] CI `.github/workflows/ci.yml`: matrix `windows-latest`, `macos-latest`, `ubuntu-latest`, `dotnet build` + `dotnet test`, upload screenshots.
- [x] `CLAUDE.md` with goal, architecture rules, projects, commands (copy CinnabarSharp's layout).
- [x] `README.md`, `LICENSE` (MIT), `CHANGELOG.md`.

**Validation**

- [ ] `dotnet test` green on the 3 OSes, locally and in CI.

## Phase 1 — Avalonia shell

- [ ] `ImmeubleCad.Desktop` (Avalonia 12, CommunityToolkit.Mvvm), composition root `AppServices.Build()` shared by `App` and the UI tests.
- [ ] Main window: menu bar, toolbar with a **2D / 3D / Wireframe** segmented control, left tool strip, **level bar** (vertical, elevator-style buttons, top level first) next to the view, center view area showing the plan view by default (3D and elevation views are placeholders for now), right panels (Networks, Properties), status bar (tool, cursor position in metres, current level, zoom, **contextual hint** of what to do next).
- [x] `ViewState` in Core with `ViewStateChanged` events; `MainViewModel` exposes it to both views. *(not yet wired to the views)*
- [ ] Menus defined once as `MenuSpec` records, turned into a `NativeMenu` on macOS and an in-window `Menu` elsewhere (copy CinnabarSharp). Later features bound to a disabled `NotYetImplemented` command.
- [ ] Shortcuts through `PlatformHotkeyConfiguration.CommandModifiers` (⌘ on macOS, Ctrl elsewhere).
- [ ] `IDialogService` (file pickers, save-changes prompt, errors), `FakeDialogService` for tests, `TestHarness` building the real window.
- [ ] Welcome screen: New building, Open, Recent files.

**Log file (from the first phase, so every later bug leaves a trace)**

- [x] `AppLog` (Desktop): `ILoggerProvider` writing to `log.txt` in the per-OS app-data folder (`%LOCALAPPDATA%\ImmeubleCad`, `~/Library/Application Support/ImmeubleCad`, `~/.local/share/ImmeubleCad`). Line format: ISO timestamp, level, category, message, exception with stack trace. Writes are buffered on a background queue and flushed on error and on exit. *(partial: file provider + global handlers; no env-var levels/stderr mirror)*
- [x] Rolling: new file when `log.txt` exceeds 5 MB, keep the last 5 (`log.1.txt`…); old files deleted at startup.
- [ ] Levels: Information by default, Debug with `IMMEUBLECAD_LOG=debug` or a setting; also mirror to stderr with `IMMEUBLECAD_LOG=1`.
- [ ] Global handlers log everything that escapes: `AppDomain.CurrentDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, `Dispatcher.UIThread.UnhandledException`, Avalonia's own warnings/errors (log sink). Errors shown in dialogs are logged too.
- [ ] Startup line with app version, build date, OS, architecture, .NET version, GPU/OpenGL info once the 3D view starts; one line per opened/saved file (path) and per long operation (import, detection, export) with its duration.
- [ ] Help › Open Log Folder (application menu on macOS) and Help › Copy System Info (version, OS, last 200 log lines) for bug reports.
- [ ] Never log plan images, building contents or typed text; file paths and operation names only.
- [ ] Tests: the provider writes, rolls and survives concurrent writes; the global handlers log an unhandled task exception (Desktop.Tests with a temp log folder, never the user's).

**Validation**

- [ ] App launches on the 3 OSes; HiDPI crisp; macOS app menu present.
- [ ] Headless test `Main_window_shows_three_views` passes on the 3 OSes.

## Phase 2 — Building document, file format, history

- [x] Model (Core): `Building` → `Level` (name, altitude mm, height mm, background image, calibration) → `Wall`; `Equipment`; `Network` → `Node`, `Segment` (see Phase 6 for networks; create the types now, empty).
- [x] `Wall`: footprint polygon (`List<PointMm>`, closed) on one level, height defaults to the level height, optional label. Walls from detection and walls drawn by hand share this type.
- [x] `Opening` (door, window, simple opening) hosted by a wall: position along the wall, width, height, sill height, swing side and direction for doors, optional number/label.
- [x] `Space` (room): closed polygon on a level (computed from the surrounding walls or drawn), name, **number** (e.g. cellar "Cave 12"), kind (cellar, corridor, boiler room, bike room, parking, stairwell, service shaft, apartment, room…), optional lot number (copropriété).
- [x] **Stability** on walls, openings and spaces: `Common` (common parts: cellars, corridors, stairwell, boiler room, shafts, façades: they don't change over time) or `Private` (apartment interiors: may change with renovations). Defaults from the level kind and the space kind; editable.
- [x] History: `IHistoryItem`, `BuildingHistory` (pointer, clean pointer, `IsDirty` derived), first item `BaseHistoryItem` ("New building" / "Open building").
- [x] `BuildingActions`: add/delete/rename level, change altitude/height, add/delete/move wall. `HistoryTests.Actions` lists them all and checks exact undo/redo.
- [x] Events: `IBuildingEventsService` with `BuildingEventItem`, `LevelEventItem`, `NetworkEventItem`; `MainViewModel` subscribes.
- [x] File format `.immeuble`: a zip with `building.json` (source-generated `System.Text.Json`, schema version field) and the background images of each level under `images/`. Round-trip test.
- [ ] Tabs for several open buildings, `*`/dot for unsaved changes, save prompt on close and quit, File › Open Recent, drag and drop, file on the command line.
- [ ] Undo/Redo menu and toolbar, History panel (click a step to jump to it).

**Validation**

- [ ] Save, close, reopen: model identical (automated round trip).
- [ ] 200 undo/redo cycles on a 10-level building stay responsive.

## Phase 3 — Import plans and detect walls

**3a — Copy detection code from CinnabarSharp**

- [x] Copy `LineExtraction` (and `SuggestThickness`) from `CinnabarSharp.Core/Photo` and `MaskTracer` from `CinnabarSharp.Vector` into `ImmeubleCad.Plan`. Replace any CinnabarSharp type (`ColorBgra`, `VectorPath`, selection mask…) with small local types: input is a grayscale `byte[]` + width/height, output is polygons in pixels. *(done from the local CinnabarSharp checkout)*
- [ ] Copy the matching tests and synthetic plans from `CinnabarSharp.Core.Tests` / `CinnabarSharp.Vector.Tests`; keep pinned checksums. *(not copied: new synthetic tests written; checksums pinned on them)*
- [x] `PROVENANCE.md`: source repository, commit hash, list of copied files and what was changed. Keep license headers (and Pinta's, if any copied file has them) and add a `THIRD-PARTY-NOTICES.txt` entry.
- [ ] Interface in Core: `IWallDetector { WallDetectionResult Detect(GrayImage image, WallDetectionOptions options, CancellationToken ct); }`, implemented by `PlanWallDetector` (adapter over `ImmeubleCad.Plan`). Core decodes the JPG with Magick.NET and passes pixels; `ImmeubleCad.Plan` never sees a file.

**3b — Import wizard (one level at a time)**

- [ ] File › Import Plan…: pick a JPG/PNG/PDF page (PDF optional), choose or create the level (name, altitude, height; defaults: RDC at 0, next level +2 700 mm).
- [ ] Step 1, straighten: Auto deskew (detect dominant orthogonal lines) with manual angle slider.
- [ ] Step 2, calibrate: click two points on the image, type the real distance in metres → pixels per mm. Optional second measure along the other axis (a horizontal and a vertical dimension) → **separate X and Y scales**, because scanned or printed plans are often stretched differently in length and width. Warn if not calibrated.
- [ ] `Level.ImageTransform`: the background image is placed with a full **affine transform** (translation, rotation, scale X, scale Y, optional shear), stored as a 2×3 matrix in `ImageTransform` (Core, doubles allowed here since it maps image pixels to mm; results rounded to mm). A **perspective** mode (homography from 4 points) is available for phone photos taken at an angle.
- [ ] Step 3, detect: live preview of the wall mask in red over the faded plan; Auto thickness button; sliders for threshold, min thickness, bridge gap, min area.
- [ ] Step 4, review: detected walls shown as polygons over the plan; click to delete, lasso to delete, accept all. Validation adds the walls in **one history step** (`BuildingActions.ImportWalls`).
- [ ] The plan image stays as the level's background (opacity slider, hide/show).

**3c — Align and stretch levels**

JPG plans of different levels rarely fit exactly on top of each other (different scan or print scale, paper shrink, photo angle). The user must be able to stretch a plan in length or width until it matches the level below.

- [ ] Level › Align with Level Below, by points: click pairs of matching points (a point on this level's plan, then the same point on the ghost of the level below: stairwell corners, risers, façade corners). Fit chosen by the number of pairs: 2 pairs = translation + rotation + uniform scale; **3 pairs or more = affine (independent X/Y stretch)**, least squares; 4 pairs in perspective mode = homography. Show the residual error per pair (in mm, red when above 50 mm) so a wrong click is visible; drag a pair to fix it. `ImageFit` in Core, pure, unit-tested with synthetic transforms (exact recovery, noise, degenerate cases like collinear points).
- [ ] Manual stretch mode: the background image gets 8 handles (like a picture in an image editor). Corner handles scale both axes (Shift keeps the ratio), **side handles stretch in one direction only (length or width)**, inside drag moves, a rotation handle rotates. Snaps to the ghost of the level below. Arrow keys nudge by 1 mm (Shift: 10 mm). Live numeric fields for scale X, scale Y, rotation, offset in the Properties panel.
- [ ] Overlay helpers while aligning: ghost of the level below in a contrasting color, blend/difference mode between the two plans, opacity slider.
- [ ] Walls detected from a plan are stored in **image pixels + the level's transform** until the user confirms; when the transform changes after detection, ask "Move the walls with the plan?" (default yes). Walls drawn by hand never move.
- [ ] Every alignment or stretch is one history step (`BuildingActions.SetImageTransform`, in `HistoryTests.Actions`).

**3d — Wall fitting (optional for the MVP, needed for clean editing)**

- [ ] `WallFitter` (Plan): from traced outlines, find pairs of parallel sides → wall center segments with a thickness; keep the polygon when fitting fails.

**3e — Straighten walls (clean up scan artifacts)**

Walls traced from a scan are often slightly crooked or made of many short broken segments ("saw teeth"). The user selects one or several walls and makes them straight, with as few segments as possible.

- [x] `WallStraightener` (Plan, pure, deterministic), applied to a wall polygon in steps:
  1. **Dominant axes:** histogram of edge directions weighted by length over the whole level → the building's main axis (and its perpendicular), so "straight" means aligned with the building, not with the image. The straightener takes an optional **`ReferenceGeometry`** input (a set of reference directions and outline edges): empty in the MVP, filled from the cadastral footprint in Phase 12, so buildings with non-90° corners (common in old city centres) get their real angles instead of being forced to 90°.
  2. **Remove saw teeth:** drop jogs and zigzags smaller than a tolerance (default 30 mm, slider), Douglas–Peucker on what remains.
  3. **Fit lines:** group the remaining points into runs, fit each run with a least-squares line (robust to outliers: RANSAC or iterative trimming).
  4. **Snap angles:** lines within N degrees (default 5°, slider) of the main axes become exactly parallel or perpendicular to them; 45° optional.
  5. **Merge:** collinear neighbor lines become one; corners rebuilt as line intersections.
  6. **Regularize thickness (option):** two parallel faces of a wall are made exactly parallel and the thickness rounded (to 10 mm, or to a standard list: 50, 70, 100, 150, 200, 250, 300…).
- [ ] Straighten tool and Wall › Straighten command (shortcut): works on the selection (click, Shift+click, box or lasso, or all walls of the level). Live preview: the straightened outline drawn over the original in another color, with the segment count before → after (e.g. "47 → 4 segments"). Tolerance and angle sliders in the options bar; Enter applies, Escape cancels. One history step for the whole selection.
- [ ] "Make rectangular" for a single straight wall: replace it by the best-fitting rectangle (oriented minimal-area rectangle on the main axis), i.e. one clean wall with a center line and a thickness.
- [ ] Optional at the end of the import wizard: "Straighten all detected walls" checkbox with the same settings (on by default for scanned plans).
- [ ] Adjacent walls keep touching: after straightening, endpoints within the tolerance of another wall's face are snapped onto it (no gaps or overlaps at T and L junctions).
- [ ] Tests on synthetic walls: noisy straight wall → 4 segments; L-shaped wall with saw teeth → 6 segments; wall rotated 12° → straightened along 12°, not 0°; tolerance 0 → unchanged; determinism (pinned output).

**Validation (3e)**

- [ ] On the scanned A4 and the phone photo plans, "Straighten all" reduces the segment count by at least 80 % and no wall moves by more than the tolerance; a few clicks fix the remaining crooked walls.

**Validation**

- [ ] Five real plans (clean CAD export, scanned A4, phone photo at an angle, hand drawing, the author's building): at the suggested settings, every load-bearing wall is detected and text/dimensions are dropped; record failures in `docs/detection-results.md`.
- [ ] A 3-level building imported from 3 JPGs: after alignment, the stairwell walls are superposed within 50 mm.
- [ ] A plan scanned with a 3 % stretch in width only: 3-point alignment brings it within 50 mm of the level below; the side handle alone does the same by hand in under a minute.
- [ ] Detection of an A3 plan at 300 dpi takes a few seconds at most, with live preview, on each OS.

## Phase 4 — Plan view and walls by hand

- [x] `PlanView` control: background image, walls (filled polygons), grid, zoom around the mouse (⌘/Ctrl + wheel, pinch), pan (Space + drag, middle mouse). *(grid, walls, ghost of level below, zoom/pan; no background image yet)*
**Moving between levels**

- [x] Level bar: one button per level (name + altitude in the tooltip), current level highlighted; click to change level; Page Up / Page Down go one level up/down; the change keeps the zoom and the look-at point (`ViewState`). *(scrollable, tested with 30 levels; no right-click menu, no tooltip)*
- [ ] Ghost of the level below drawn faded under the current level (on by default); ghost of the level above optional. Toggles in the View menu.
- [ ] Risers crossing the current level show a small up/down arrow; clicking it moves to the next level the riser reaches, keeping the riser centered.
- [ ] Right-click on the level bar: add level above/below, rename, set altitude/height.

**Input model (hybrid click/drag)**

- [x] `ToolStateMachine` base in Core: `Idle → Pressed → Dragging | Placing → Committed`, drag threshold in screen pixels passed by the view (default 4 px), so click mode and drag mode come for free in every tool. Tests for both paths and for the threshold.
- [ ] Chained tools (Draw Wall, later Draw Pipe): each click adds a point and starts the next segment; double-click or Enter finishes; Backspace removes the last point; Escape cancels the segment in progress (a second Escape cancels the whole run); clicking the first point closes the outline.
- [x] Rubber band preview with live length and angle shown next to the cursor. *(length only, no angle)*
- [ ] Typed values while placing: typing a number shows an input box near the cursor; Enter applies it as the segment length along the current direction (`2.4` = 2.4 m, `2400mm` accepted). Tab switches to the angle field.
- [ ] Right-click while placing: context menu Finish / Undo last point / Close outline / Cancel.
- [ ] Status bar hint per tool state (e.g. "Click the next point — Enter to finish — Esc to cancel"), translated.

**Tools**

- [x] Tools in Core implementing `ITool` with `ToolPointer` in **mm** (no Avalonia types), unit-tested: Select (click, box by drag, Shift add), Move (drag, or click then click destination), Draw Wall (chained, thickness option), Draw Room (rectangle by drag or two clicks → 4 walls), Delete. *(Select with box + group move, Draw Wall chain; no Move-by-click, Draw Room, Delete tool)*
- [x] Snapping (`Snapper` in Core): grid, wall corners, wall faces, midpoints, orthogonal 0/90° (45° with a modifier), alignment with existing points (dotted guide line), and points of the ghost level. Snap indicator (small symbol per snap kind) drawn by the view from a `ToolOverlay`.
- [ ] Measure tool: two clicks → distance shown, nothing added to the model.

**Doors, rooms and cellars**

- [ ] Level kind: Basement/cellars, Ground floor, Standard floor, Attic, Roof, Site. A basement level defaults to `Common` stability and its spaces to "Cellar".
- [ ] Door tool: click on a wall → door inserted at that position (default 800 mm for apartments, 700 mm for cellars, editable); the wall gets the opening (cut in 2D, hole in the 3D mesh); swing arc drawn in plan; click the door then the arc to flip the swing side; drag along the wall to move it, side handles to change the width. Windows and simple openings work the same way. *(Core `AddOpening`/`FlipOpening` done; no tool, no cut in 2D/3D)*
- [ ] Space tool: click inside an area closed by walls → space created from its outline (flood fill on the walls polygon set, `SpaceFinder` in Core, tested), or drawn by hand. Name and number shown at the center of the space in plan. *(Core `SpaceFinder` flood fill done; no tool)*
- [ ] **Cellar numbering**: "Number cellars" command on a basement level: click the cellars in order (or let it number them along the corridor) → "Cave 1, 2, 3…" with a configurable prefix and start; the number is also put on each cellar's door. Renumber keeps one history step. *(Core action `NumberSpaces` done; no UI command)*
- [ ] Display options: show/hide private interiors; private elements drawn lighter with a "may have changed" hint, common parts drawn solid. Filter in 2D and 3D: "Common parts only" (useful for the conseil syndical and for networks that run through common parts). *(Core filter `Spaces.CommonWalls/CommonSpaces`; no UI toggle)*
- [ ] Spaces list panel: per level, kind, number, lot, area (computed), with a click to zoom on it; export as CSV (no owner names: lot numbers only). *(CSV export in Core `Spaces.ToCsv`; no panel)*
- [ ] Networks know where they run: each segment gets the space(s) it crosses (computed), so the bill of materials and the network panel can say "riser in Cave 12" or "shut-off valve in the boiler room". *(`Spaces.CrossedBy`; not shown in panels yet)*
- [ ] Properties panel: selected wall's thickness, height, label.

**Validation**

- [ ] Draw a 4-room flat by hand in under 5 minutes, by someone who has never used the app (usability check).
- [ ] Snapping tests pass on the 3 OSes (pure Core).
- [ ] On the author's basement plan: all cellars become numbered spaces with a door in under 15 minutes; "Common parts only" shows the basement, stairwell and shafts of every level.

## Phase 5 — 3D view

- [ ] `ImmeubleCad.Scene`: `MeshBuilder` producing vertex/index buffers (float positions in metres, normals, color per network/category): wall extrusion from footprint polygon and height, floor slab per level (from the outer outline or a bounding polygon), pipe cylinders, fittings (torus segment for elbows, simple tee), equipment boxes. Deterministic; pinned checksum test.
- [x] `Picking`: ray vs meshes → id of the wall, equipment, node or segment under the mouse. Unit-tested.
- [ ] `Viewport3D` control (Avalonia `OpenGlControlBase` + Silk.NET.OpenGL): orbit (left drag), pan (right or middle drag), zoom (wheel), "fit all", top/front/side/isometric presets. Simple lighting (one directional light + ambient).
- [ ] Render modes: **Solid**, **X-ray** (walls translucent, networks opaque), **Wireframe** (wall edges + networks in color). `Scene` produces an edge list per mesh for wireframe.
- [ ] Level filters: current level only / current + below / whole building; **cut above the current level** (dollhouse view) on by default; show/hide each network.
- [ ] Level bar works in 3D too: changing level moves the cut and the camera target vertically.

**Switching 2D ↔ 3D without losing context**

- [ ] One key (Tab) and the toolbar segmented control switch 2D / 3D / Wireframe; the choice is in `ViewState`.
- [ ] Animated transition (~0.5 s, ease-in-out): the 3D camera starts as an orthographic top view matching the plan view's framing (same look-at point and scale), then tilts to the last used 3D angle (default isometric). Going back to 2D reverses it and restores the plan exactly where it was. Animation can be turned off in settings.
- [ ] Selection, current level and look-at point are shared: selecting in one view highlights in the other; "Show in 3D" / "Show in plan" on a selected object frames it.
- [ ] Optional side-by-side mode (plan left, 3D right, synchronized through `ViewState`) for large screens.
- [ ] Tools work in both views: the 3D view converts the pointer to mm on the current level's plane (or on the constrained axis for handles) and feeds the same Core tools.
- [ ] Headless tests: when OpenGL isn't available, render a fallback (wireframe with Avalonia drawing) so screenshots still show something; test the camera math in Scene.

**Validation**

- [ ] A 6-level building with all its walls orbits at 60 fps on an integrated GPU, on each OS.
- [ ] Clicking a wall in 3D selects it in the plan view.
- [ ] Switching 2D → 3D → 2D ten times in a row: the plan comes back at exactly the same position and zoom; the transition is smooth on each OS.
- [ ] A beginner finds a pipe crossing a slab between two levels using the level bar and X-ray mode without help.

## Phase 6 — Networks model

- [x] `NetworkType`: HeatingSupply, HeatingReturn, DomesticHotWater, ColdWater, Drainage, Gas (extensible), each with default color, default diameter and material.
- [x] `Network` = graph: `Node` (id, `PointMm3` position, optional attachment: wall face, equipment port) and `Segment` (node A, node B, outer diameter mm, material). Segments must be axis-aligned (X, Y or Z) in the MVP; 45° later.
- [x] `FittingResolver` (Core, pure): derives each node's fitting from its segments: end cap, straight joint, elbow 90°, tee, cross, reducer (diameter change), floor/slab crossing (vertical segment crossing a level's altitude), wall crossing. Fully unit-tested.
- [x] Validation rules (warnings, not errors): unconnected end, segment through a wall without crossing, two networks overlapping, slope missing on drainage.
- [x] `BuildingActions` for networks: add node/segment, move node, split segment, merge collinear segments, delete, change diameter/type. All in `HistoryTests.Actions`.
- [ ] Networks panel: list of networks, color, visibility, lock.

**Validation**

- [ ] `FittingResolver` tests cover every node configuration; the resolved fittings of a sample building are pinned.

## Phase 7 — Object palette, equipment and risers

**Palette**

- [ ] Palette panel (left, collapsible) with categories: **Structure** (wall, partition, door, window, slab, stairs, service shaft), **Networks** (pipe, riser, riser templates), **Valves & fittings** (shut-off valve, thermostatic valve, balancing valve, air vent, check valve, pressure reducer, meter), **Equipment** (radiator, boiler, hot-water tank, manifold, circulator pump), **Sanitary** (washbasin, sink, WC, shower, bathtub). Search box, "Recent" section at the top, large icons with labels (beginner friendly).
- [ ] Two kinds of entries: **drawing tools** (wall, partition, pipe, riser: clicking the entry activates the tool) and **objects to drop** (everything else: drag onto the view, or click the entry then click in the view, using the hybrid input).
- [ ] Object catalog as JSON in resources (`catalog/*.json`, source-generated deserialization): id, category, name (translated), parameters with defaults (width, height, diameter…), **2D symbol** (simple vector path, standard plumbing/heating symbols), **3D shape** (combination of boxes and cylinders built by `ImmeubleCad.Scene`), typed **ports** (network type + diameter + position/direction). Catalog test: every entry loads, has a symbol, a shape and valid ports.
- [ ] Drop rules (`PlacementRules` in Core, unit-tested): doors/windows snap into walls; radiators and sanitary objects snap against a wall face at a default height (radiator 150 mm above floor), oriented along the wall; **inline components (valves, meters, pumps) only drop onto a pipe**: they snap to it, split the segment, align with its axis and take its diameter. In the network graph an inline component is a **node with a `Component`**; it is counted in the bill of materials.
- [ ] Preview while dragging: ghost of the object, green when the placement is valid, red with a hint when not ("A valve must be placed on a pipe").
- [ ] User objects (later): duplicate a catalog entry, change its parameters, save it in the user's catalog folder.

**Equipment**

- [ ] Equipment model: instance of a catalog entry with its parameter values, position, level, orientation, host (wall face, pipe) and ports.
- [ ] **Riser (colonne)** is not a separate type: it is a set of vertical segments of one network. The Riser tool creates it (see Phase 8a).
- [ ] Connect: drag from a radiator port to a riser or a pipe → creates the connection segments with orthogonal routing (down to a chosen height, along the wall, to the riser). One history step. *(Core `ConnectOrthogonal`; no drag UI)*
- [ ] Properties panel: equipment dimensions, ports, level, label (e.g. "Radiateur séjour").

**Validation**

- [ ] On the author's building: place the radiators of one flat and connect them to the existing heating risers in under 10 minutes.

## Phase 8 — Pipe tools (the core of the UX)

**8a — Risers from the top view (circles)**

- [ ] Riser tool in the plan view: click to place a circle = a vertical pipe at that XY. Options bar: network type, diameter, **from level / to level** (defaults: whole building).
- [ ] Templates: place 2 or 3 risers at once with fixed spacing (e.g. hot + cold water, heating supply + return, or hot + cold + drainage), editable spacing and order. Saved as user templates. *(Core `AddRiserTemplate`; no UI, no saved user templates)*
- [ ] Riser snaps to wall corners and faces with an offset (default: 50 mm from each face).

**8b — Pipe on a wall corner from the elevation view**

- [ ] Elevation view: pick a wall (or two walls meeting at a corner) and see it from the front, with level lines and altitudes.
- [ ] In the elevation view, click the corner (or a point on the face) → vertical pipe attached to that corner, offset from both faces; drag to set its top and bottom, snapping to level altitudes.
- [ ] Horizontal pipe along the wall face at a given height above the floor.

**8c — Extent over several levels**

- [ ] Every vertical run has an editable extent "from level X to level Y" (Properties panel) and top/bottom handles in elevation and 3D views that snap to level altitudes; slab crossings are added automatically.

**8d — Direct manipulation with handles**

- [ ] Draw Pipe tool: chained click mode like Draw Wall (each click = a new segment and an elbow), with the same Enter/Backspace/Escape/typed-length behavior; segments constrained to X/Y in plan, Z in elevation.
- [ ] Handles use the hybrid input: drag the handle (Paint style), or click the handle then click the destination (AutoCAD style).
- [ ] End handle: drag to **extend** along the segment axis; the length is shown live; snapping to walls, other pipes and level altitudes.
- [ ] End handle + modifier (Ctrl / ⌥ on macOS) or drag perpendicular past a threshold: **create an elbow** and a new segment along the new axis (X, Y or Z); keep going to chain elbows.
- [ ] Middle of a segment: drag to **create a tee** and a branch.
- [ ] Move a whole run perpendicular to itself (keeps connections by stretching the connected segments).
- [ ] Type a length while dragging (e.g. "1.25" Enter) for precision.
- [ ] Each gesture = one history step. The tool previews by quiet model changes and commits on pointer-up (same pattern as CinnabarSharp's editable tools).
- [ ] The same handles work in the plan, elevation and 3D views (3D: drag constrained to the axis under the mouse, axis gizmo shown).

**Validation**

- [ ] A beginner lays the heating supply/return of a 3-level building from scratch (risers + one flat's radiators) without help, in under 20 minutes; list friction points.
- [ ] Tool tests in Core cover extend, elbow, tee, move-run, typed length, snapping to level altitudes.

## Phase 9 — Bill of materials and exports

- [x] Bill of materials: per network, length per diameter and material (rounded up to standard bar lengths, option), count of each fitting type, list of equipment. View in a panel, export to CSV and to a printable PDF. *(partial: pipe length per network/diameter + CSV; no fittings/PDF/bar lengths)*
**Exports to other tools** (File › Export, one exporter per format behind `IBuildingExporter` in Core, registered in DI like CinnabarSharp's formats; each has a round-trip or golden-file test)

- [x] **IFC 4** (`.ifc`), the BIM standard, priority export: building → storeys (`IfcBuildingStorey` per level) → `IfcWall` (extruded footprints), `IfcSlab`, `IfcDoor`/`IfcWindow`, networks as `IfcDistributionSystem` with `IfcPipeSegment`, `IfcPipeFitting` (elbows, tees from `FittingResolver`), `IfcValve`, `IfcSpaceHeater`, `IfcBoiler`…, colors per network, geographic placement (`IfcSite` with the Lambert 93 anchor once Phase 12 exists). Opens in FreeCAD (BIM workbench), Revit, ArchiCAD, BlenderBIM/Bonsai, online IFC viewers. Library: evaluate xBIM Essentials and GeometryGym IFC (check licenses and AOT compatibility before choosing; write an ADR in `docs/adr/`). *(partial: hierarchy, walls, pipe segments only; no geometry/fittings/colors)*
- [x] **FreeCAD macro** (`.FCMacro` / `.py`): a generated Python script that rebuilds the building as **native, editable FreeCAD objects** (BIM/Arch walls on levels, pipes as swept profiles or BIM pipes, equipment as parametric boxes, one group per network with its color). Best fidelity for FreeCAD users; no FreeCAD dependency at build time (text generation only).
- [x] **DXF** (`.dxf`): 2D plans per level, one layer per category and per network (AutoCAD, LibreCAD, DraftSight, QCAD). Library: netDxf (check license) or a minimal hand-written DXF R12 writer. *(partial: LINE entities per layer; no R12 header/units)*
- [x] **glTF** (`.glb`) and **OBJ**: 3D meshes from `ImmeubleCad.Scene` for Blender, SketchUp, web viewers, 3D printing; one node per level and per network so they can be hidden in the target tool. *(OBJ + GLB in Core/MCP; no UI menu entry for GLB yet)*
- [x] **SVG / PDF**: printable plans per level and per network (title block with level name, scale, north arrow, legend). *(SVG per level in Core; PDF only for the bill of materials; no title block / north arrow)*
- [x] **CSV / PDF**: bill of materials (above).
- [ ] Export dialog: format, levels to include, networks to include, units (mm/m), with a short line explaining which tool each format is for (beginner friendly).
- [ ] MCP tools `export_ifc`, `export_freecad_macro`, `export_dxf`, `export_gltf` (Phase 10).

**Imports from other tools (later)**

- [ ] Reference model: import a glTF/OBJ/IFC (e.g. the author's existing FreeCAD model) as a locked, non-editable reference mesh to trace over, per level.
- [ ] IFC import of walls and storeys (editable), once the IFC export is stable.

**Validation**

- [ ] The bill of materials of the sample building matches a hand count.
- [ ] The IFC of the sample building opens in FreeCAD and in an online IFC viewer: storeys, walls, pipes, fittings and valves are present with the right types, positions and colors.
- [ ] The FreeCAD macro runs in FreeCAD 1.x without errors and produces editable walls and pipes at the right scale.
- [ ] The DXF opens in LibreCAD/QCAD at the right scale with one layer per network; the glTF opens in Blender and a browser viewer.

## Phase 10 — MCP server

- [x] `ImmeubleCad.Mcp` (official C# SDK), Core only, hosted by the app: `ImmeubleCad --mcp` (headless, stdio) and `--mcp --attach` (relay to the running app over a Unix domain socket, File › Allow AI Agents, off by default). Copy the design from `CinnabarSharp.Mcp` (dispatcher, `McpContext.Run`, allowed folders, `overwrite`/`discardChanges`). *(partial: stdio only; no `--attach`)*
- [x] Tools: open/new/save/close building, list levels, add level, import plan with detection parameters, list walls, add riser (network, XY, level range), add pipe run (list of points), connect equipment, bill of materials, export glTF/SVG/CSV, undo/redo. *(partial: add level/wall/riser/pipe run, save, export, undo/redo, BOM; no open/import)*
- [x] Resources: `immeuble://buildings`, `immeuble://buildings/{id}/networks`, `immeuble://equipment-library`.
- [x] `docs/mcp.md` with `claude mcp add` setup and examples ("add a hot and cold water riser from RDC to R+5 in the corner of the bathroom").
- [ ] `ImmeubleCad.Mcp.Tests`: start the real app with `--mcp` and drive it end to end.

**Validation**

- [ ] Claude Code builds a 2-level building from two JPGs and adds risers through MCP on each OS.

## Phase 11 — Polish and packaging

- [ ] Settings persistence (JSON in the per-OS app-data folder): window, panels, last tool, snapping options, templates.
- [ ] App icon, About dialog, light/dark themes (design tokens like CinnabarSharp's `Themes/`).
- [ ] `packaging/package.sh <rid> <version>`: self-contained, Native AOT, Windows zip, macOS `.app`/`.dmg` (single architecture), Linux tarball with `.desktop` file; `.immeuble` file association.
- [ ] Release workflow: pushing a `v*` tag packages win-x64, linux-x64, osx-arm64, osx-x64 and publishes a GitHub release.
- [ ] Unsaved work recovery (autosave every few minutes to the app-data folder, offered at next start after a crash); the crash itself is already in the log (Phase 1).
- [ ] Onboarding: a sample building opened from the welcome screen, and a 5-step guided tour (import a plan, calibrate, detect, add a riser, extend a pipe).

**Validation**

- [ ] Download the release packages on fresh machines of each OS: the app starts, imports a plan, saves and reopens a building.

## Phase 12 — Site, map and cadastre (after the MVP)

Place the building on its real plot, using French open data (IGN Géoplateforme, Licence Ouverte 2.0).

- [ ] Georeferencing in the model: `Building.Site` = anchor point in **Lambert 93 (EPSG:2154, metric)** + rotation (true north) + optional altitude. Model coordinates stay in mm relative to the anchor; conversions WGS84 ↔ Lambert 93 only at the import/export boundary (`Lambert93` helper in Core, pure C#, tested against reference points).
- [ ] Interfaces in Core: `IGeocoder` (address → coordinates), `ICadastreService` (parcel and building footprints around a point, GeoJSON), `IMapTileSource` (WMTS tiles: orthophoto, IGN plan, cadastral parcels). Implementations in a separate `ImmeubleCad.Geo` project (HttpClient, no UI); fakes in tests. Tiles and GeoJSON cached on disk so the app works offline once loaded.
- [ ] **Site level** in the level bar, below the lowest level: shows the chosen background (orthophoto, IGN plan or cadastre) under the building footprint, with parcel boundaries and cadastral buildings as vector outlines.
- [ ] Place the building: type an address → zoom on the plot → click two corners of the building on the plan and the matching corners of the cadastral footprint → anchor and rotation computed. One history step. Or pick the cadastral footprint directly to start a new building from it (outer walls pre-drawn).
**Cadastre-assisted geometry (the cadastral footprint as a guide for angles and dimensions)**

- [ ] Once the building is placed, the cadastral footprint (PCI `batiment` layer) becomes a **reference outline** for every level: shown as a dashed overlay in the plan view, togglable.
- [ ] Reference angles: the footprint's edge directions feed `ReferenceGeometry`, so Straighten (3e) snaps outer walls to the **real angles of the building** (e.g. 84° or 97° corners) instead of 90°; interior walls still snap to the main axes.
- [ ] Snap outer walls to the footprint: the Snapper offers footprint edges and corners; a "Fit outer walls to cadastre" command moves the outer face of façade walls onto the footprint edges when they are within a tolerance (default 300 mm), with preview and one history step.
- [ ] Check the plan's scale and stretch: affine fit of the detected outer outline onto the footprint → suggested scale X / scale Y / rotation, shown as "your plan is 2.8 % wider than the cadastre — apply?". Feeds the alignment of Phase 3c (the footprint acts as a common reference for all levels, so each level can be aligned on it directly).
- [ ] Angle check: list the corners whose angle differs from the footprint by more than N degrees (default 2°), with a click to zoom on each.
- [ ] Shared walls (mitoyenneté): footprint edges shared with a neighbouring cadastral building are marked, so the user knows these walls are party walls.
- [ ] The cadastre is a **guide, not the truth**: its precision varies between communes and footprints can merge adjoining buildings or omit recent extensions. Every cadastre-based correction is a suggestion with preview, never applied silently; the user's plan wins when they disagree.
- [ ] `FootprintMatcher` (Plan, pure): match plan outline edges to footprint edges (direction + distance), robust to missing or extra edges; tests on synthetic footprints with rotation, stretch, a missing edge and a merged neighbour.
- [ ] North arrow in the plan view and in exports; plan view can be shown "north up" or "building aligned".
- [ ] Connections from the street (later): water, gas, sewer entry points on the plot boundary, distances to boundaries.
- [ ] IFC export uses the anchor for `IfcSite` geographic placement.
- [ ] Attribution of the data sources shown on the map (IGN, DGFiP) and in exports.

**Validation**

- [ ] The author's building placed from its address: the plan matches the cadastral footprint within 0.5 m.
- [ ] On a building with non-90° corners, Straighten with the cadastre reference keeps the real corner angles (within 1°), and the scale/stretch suggestion matches a manual calibration within 1 %.
- [ ] With the network off, a building opened before still shows its site from the cache.

## Criteria for the MVP (0.1)

- [ ] Import JPG plans for several levels, calibrate, detect walls, align levels.
- [ ] 3D view of the building with level visibility and X-ray mode.
- [ ] Radiators from the library, risers from the top view (with templates), pipes on wall corners from the elevation view, level extents, extend/elbow/tee with handles.
- [ ] Object palette with valves dropped onto pipes.
- [ ] Bill of materials export, IFC export and FreeCAD macro export.
- [ ] Phases 0 to 9 validated on the 3 OSes (Phase 12 comes after the MVP).