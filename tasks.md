# ImmoCad — Roadmap

Goal: a desktop application, simple enough for a beginner, to model a building (house or apartment block) from its floor plans and lay out its networks: heating pipes, hot and cold water, drainage. It runs natively on **macOS, Linux and Windows**.

The user flow is: **import JPG plans → walls and levels are built → place radiators and risers (colonnes) → lay pipes → get a 3D view and a bill of materials.**

The application skeleton, conventions and quality bar follow **CinnabarSharp** (https://github.com/pgourlain/CinnabarSharp): same stack, same project split, same "Core is non-visual" rule, same "Actions are the only way to edit a document" rule, same per-OS validation checklists, same MCP server design. When in doubt about how to structure something, look at how CinnabarSharp does it.

## Stack decision

**Avalonia UI 12 on .NET 10 (LTS), MVVM with CommunityToolkit.Mvvm, 3D with Silk.NET (OpenGL) inside an Avalonia `OpenGlControlBase`.**

- Same stack as CinnabarSharp, so the shell, dialogs, menus (native on macOS), settings, packaging and CI can be copied from it.
- 2D plan view: custom Avalonia `Control` (like CinnabarSharp's `CanvasView`), drawing with Avalonia's `DrawingContext`.
- 3D view: `OpenGlControlBase` + `Silk.NET.OpenGL`. Meshes are generated in pure C# (see `ImmoCad.Scene`), so the 3D view only uploads buffers and draws. Fall back to a simple software preview in headless tests.
- Wall detection is **copied** from CinnabarSharp (`LineExtraction` from `CinnabarSharp.Core/Photo`, `MaskTracer` from `CinnabarSharp.Vector`) to keep the project autonomous. See Phase 3.

## Architecture rules (also copy them into CLAUDE.md)

- **Units:** every length in the model is an `int` in **millimetres**. No floating point in the model; floats only for rendering. Snapping, equality and tests become exact.
- **Axes:** X east, Y north on the plan, Z up. A level has an altitude (Z of its finished floor) and a height.
- **`ImmoCad.Core` is non-visual:** document model, history, tools (with UI-independent pointer input), import, exports. Forbidden: Avalonia, SkiaSharp, Silk.NET, `System.Drawing`, dispatchers, dialogs, P/Invoke. Allowed: BCL, `Microsoft.Extensions.*`, Magick.NET (image decoding only). Enforced by `CoreArchitectureTests`.
- **`ImmoCad.Plan` and `ImmoCad.Scene` are BCL only** (no Core, no Magick.NET, no UI). Enforced by architecture tests.
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
| `ImmoCad.Core` | Building model, history, actions, tools, file format, import pipeline, exports. Non-visual. |
| `ImmoCad.Plan` | Copied and adapted wall detection: `LineExtraction` (mask), `MaskTracer` (outlines), plus `WallFitter` (outlines → wall segments). BCL only. |
| `ImmoCad.Scene` | Pure C# mesh generation: wall extrusion, slabs, pipe cylinders, fittings, equipment boxes; picking (ray/mesh). BCL only. |
| `ImmoCad.Geo` | Phase 12: geocoding, cadastre and map tiles over HTTP (IGN Géoplateforme), disk cache. No UI. |
| `ImmoCad.Mcp` | MCP server (official C# SDK `ModelContextProtocol`), Core only. Hosted by the desktop executable with `--mcp`. |
| `ImmoCad.Desktop` | Avalonia 12 app (MVVM): plan view, elevation view, 3D view, panels. Executable named `ImmoCad`. |
| `ImmoCad.Core.Tests` | xunit v3. Model, history, actions, tools, import, exports, `CoreArchitectureTests`. |
| `ImmoCad.Plan.Tests` | xunit v3. Detection on synthetic plans, pinned checksums. |
| `ImmoCad.Scene.Tests` | xunit v3. Mesh generation, picking, pinned checksums. |
| `ImmoCad.Desktop.Tests` | Avalonia.Headless + Skia, xunit v3. Drives the real window, saves screenshots per OS. |
| `ImmoCad.Mcp.Tests` | xunit v3. Starts `ImmoCad --mcp` and drives it over stdio. |

Root solution: `ImmoCad.slnx`.

## How to validate each phase

Every phase ends with a **Validation** checklist. Run it on macOS (Apple Silicon), Linux (Ubuntu 24.04, X11 and Wayland) and Windows 11, and don't start the next phase until it passes on all three. CI builds and runs every test on the three OSes and uploads the headless UI screenshots per OS (`screenshots-<os>`).

---

## Phase 0 — Foundation

- [x] Create `ImmoCad.slnx` with all projects of the table above (empty where needed), targeting `net10.0`.
- [x] `Directory.Build.props`: nullable, implicit usings, warnings as errors for nullable, `LangVersion latest`.
- [x] `CoreArchitectureTests`: fail if Core references Avalonia, SkiaSharp, Silk.NET or `System.Drawing`. `PlanArchitectureTests` and `SceneArchitectureTests`: BCL only.
- [x] DI entry point `ServiceExtensions.AddImmoCadServices()` in Core.
- [x] CI `.github/workflows/ci.yml`: matrix `windows-latest`, `macos-latest`, `ubuntu-latest`, `dotnet build` + `dotnet test`, upload screenshots.
- [x] `CLAUDE.md` with goal, architecture rules, projects, commands (copy CinnabarSharp's layout).
- [x] `README.md`, `LICENSE` (MIT), `CHANGELOG.md`.

**Validation**

- [ ] `dotnet test` green on the 3 OSes, locally and in CI.

## Phase 1 — Avalonia shell

- [x] `ImmoCad.Desktop` (Avalonia 12, CommunityToolkit.Mvvm), composition root `AppServices.Build()` shared by `App` and the UI tests.
- [x] Main window: menu bar, toolbar with a **2D / 3D / Wireframe** segmented control, left tool strip, **level bar** (vertical, elevator-style buttons, top level first) next to the view, center view area showing the plan view by default (3D and elevation views are placeholders for now), right panels (Networks, Properties), status bar (tool, cursor position in metres, current level, zoom, **contextual hint** of what to do next). *(2D / Élévation / 3D / Filaire buttons rather than a segmented control; status bar shows the hint and the cursor position)*
- [x] `ViewState` in Core with `ViewStateChanged` events; `MainViewModel` exposes it to both views. *(not yet wired to the views)*
- [x] Menus defined once as `MenuSpec` records, turned into a `NativeMenu` on macOS and an in-window `Menu` elsewhere (copy CinnabarSharp). Later features bound to a disabled `NotYetImplemented` command. *(disabled `NotYetImplementedCommand` entry for the guided tour)*
- [x] Shortcuts through `PlatformHotkeyConfiguration.CommandModifiers` (⌘ on macOS, Ctrl elsewhere).
- [x] `IDialogService` (file pickers, save-changes prompt, errors), `FakeDialogService` for tests, `TestHarness` building the real window.
- [x] Welcome screen: New building, Open, Recent files.

**Log file (from the first phase, so every later bug leaves a trace)**

- [x] `AppLog` (Desktop): `ILoggerProvider` writing to `log.txt` in the per-OS app-data folder (`%LOCALAPPDATA%\ImmoCad`, `~/Library/Application Support/ImmoCad`, `~/.local/share/ImmoCad`). Line format: ISO timestamp, level, category, message, exception with stack trace. Writes are buffered on a background queue and flushed on error and on exit. *(partial: file provider + global handlers; no env-var levels/stderr mirror)*
- [x] Rolling: new file when `log.txt` exceeds 5 MB, keep the last 5 (`log.1.txt`…); old files deleted at startup.
- [x] Levels: Information by default, Debug with `IMMOCAD_LOG=debug` or a setting; also mirror to stderr with `IMMOCAD_LOG=1`.
- [x] Global handlers log everything that escapes: `AppDomain.CurrentDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, `Dispatcher.UIThread.UnhandledException`, Avalonia's own warnings/errors (log sink). Errors shown in dialogs are logged too. *(Avalonia's own log sink still goes to Trace only)*
- [x] Startup line with app version, build date, OS, architecture, .NET version, GPU/OpenGL info once the 3D view starts; one line per opened/saved file (path) and per long operation (import, detection, export) with its duration. *(GPU/OpenGL line will come with the OpenGL view)*
- [x] Help › Open Log Folder (application menu on macOS) and Help › Copy System Info (version, OS, last 200 log lines) for bug reports. *(no macOS application-menu entry)*
- [x] Never log plan images, building contents or typed text; file paths and operation names only.
- [x] Tests: the provider writes, rolls and survives concurrent writes; the global handlers log an unhandled task exception (Desktop.Tests with a temp log folder, never the user's).

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
- [x] Tabs for several open buildings, `*`/dot for unsaved changes, save prompt on close and quit, File › Open Recent, drag and drop, file on the command line. *(file association/double-click on macOS not wired)*
- [x] Undo/Redo menu and toolbar, History panel (click a step to jump to it).

**Validation**

- [x] Save, close, reopen: model identical (automated round trip).
- [x] 200 undo/redo cycles on a 10-level building stay responsive.

## Phase 3 — Import plans and detect walls

**3a — Copy detection code from CinnabarSharp**

- [x] Copy `LineExtraction` (and `SuggestThickness`) from `CinnabarSharp.Core/Photo` and `MaskTracer` from `CinnabarSharp.Vector` into `ImmoCad.Plan`. Replace any CinnabarSharp type (`ColorBgra`, `VectorPath`, selection mask…) with small local types: input is a grayscale `byte[]` + width/height, output is polygons in pixels. *(done from the local CinnabarSharp checkout)*
- [x] Copy the matching tests and synthetic plans from `CinnabarSharp.Core.Tests` / `CinnabarSharp.Vector.Tests`; keep pinned checksums. *(not copied: new synthetic tests written; checksums pinned on them)* *(ported: same plan, same pinned SHA-256; the selection and SVG-render tests were replaced since those types are not copied)*
- [x] `PROVENANCE.md`: source repository, commit hash, list of copied files and what was changed. Keep license headers (and Pinta's, if any copied file has them) and add a `THIRD-PARTY-NOTICES.txt` entry.
- [x] Interface in Core: `IWallDetector { WallDetectionResult Detect(GrayImage image, WallDetectionOptions options, CancellationToken ct); }`, implemented by `PlanWallDetector` (adapter over `ImmoCad.Plan`). Core decodes the JPG with Magick.NET and passes pixels; `ImmoCad.Plan` never sees a file. *(returns `List<WallOutline>` = outline + holes, not `WallDetectionResult`)*

**3b — Import wizard (one level at a time)**

- [x] File › Import Plan…: pick a JPG/PNG/PDF page (PDF optional), choose or create the level (name, altitude, height; defaults: RDC at 0, next level +2 700 mm). *(JPG/PNG; no PDF; level = current level, create it first)*
- [x] Step 1, straighten: Auto deskew (detect dominant orthogonal lines) with manual angle slider.
- [x] Step 2, calibrate: click two points on the image, type the real distance in metres → pixels per mm. Optional second measure along the other axis (a horizontal and a vertical dimension) → **separate X and Y scales**, because scanned or printed plans are often stretched differently in length and width. Warn if not calibrated.
- [x] `Level.ImageTransform`: the background image is placed with a full **affine transform** (translation, rotation, scale X, scale Y, optional shear), stored as a 2×3 matrix in `ImageTransform` (Core, doubles allowed here since it maps image pixels to mm; results rounded to mm). A **perspective** mode (homography from 4 points) is available for phone photos taken at an angle. *(affine + homography math in Core; shear/perspective not exposed in the UI)*
- [x] Step 3, detect: live preview of the wall mask in red over the faded plan; Auto thickness button; sliders for threshold, min thickness, bridge gap, min area.
- [x] Step 4, review: detected walls shown as polygons over the plan; click to delete, lasso to delete, accept all. Validation adds the walls in **one history step** (`BuildingActions.ImportWalls`). *(click to exclude; no lasso)*
- [x] The plan image stays as the level's background (opacity slider, hide/show). *(opacity/visibility stored in the model; no slider UI yet)*

**3c — Align and stretch levels**

JPG plans of different levels rarely fit exactly on top of each other (different scan or print scale, paper shrink, photo angle). The user must be able to stretch a plan in length or width until it matches the level below.

- [x] Level › Align with Level Below, by points: click pairs of matching points (a point on this level's plan, then the same point on the ghost of the level below: stairwell corners, risers, façade corners). Fit chosen by the number of pairs: 2 pairs = translation + rotation + uniform scale; **3 pairs or more = affine (independent X/Y stretch)**, least squares; 4 pairs in perspective mode = homography. Show the residual error per pair (in mm, red when above 50 mm) so a wrong click is visible; drag a pair to fix it. `ImageFit` in Core, pure, unit-tested with synthetic transforms (exact recovery, noise, degenerate cases like collinear points). *(Align tool: click pairs, per-pair residual (red above 50 mm), Enter applies; Core `AlignLevel` + `ImageFit`)*
- [x] Manual stretch mode: the background image gets 8 handles (like a picture in an image editor). Corner handles scale both axes (Shift keeps the ratio), **side handles stretch in one direction only (length or width)**, inside drag moves, a rotation handle rotates. Snaps to the ghost of the level below. Arrow keys nudge by 1 mm (Shift: 10 mm). Live numeric fields for scale X, scale Y, rotation, offset in the Properties panel.
- [x] Overlay helpers while aligning: ghost of the level below in a contrasting color, blend/difference mode between the two plans, opacity slider. *(level below in blue tint while aligning/stretching, optional difference blend, opacity slider in Properties)*
- [x] Walls detected from a plan are stored in **image pixels + the level's transform** until the user confirms; when the transform changes after detection, ask "Move the walls with the plan?" (default yes). Walls drawn by hand never move. *(walls are stored in mm; the alignment tool has a "Move the walls with the plan" option, on by default; they are not kept in pixels until confirmed)*
- [x] Every alignment or stretch is one history step (`BuildingActions.SetImageTransform`, in `HistoryTests.Actions`).

**3d — Wall fitting (optional for the MVP, needed for clean editing)**

- [x] `WallFitter` (Plan): from traced outlines, find pairs of parallel sides → wall center segments with a thickness; keep the polygon when fitting fails. *(Plan only; the importer keeps the traced polygons, WallFitter is not wired into the import yet)*

**3e — Straighten walls (clean up scan artifacts)**

Walls traced from a scan are often slightly crooked or made of many short broken segments ("saw teeth"). The user selects one or several walls and makes them straight, with as few segments as possible.

- [x] `WallStraightener` (Plan, pure, deterministic), applied to a wall polygon in steps:
  1. **Dominant axes:** histogram of edge directions weighted by length over the whole level → the building's main axis (and its perpendicular), so "straight" means aligned with the building, not with the image. The straightener takes an optional **`ReferenceGeometry`** input (a set of reference directions and outline edges): empty in the MVP, filled from the cadastral footprint in Phase 12, so buildings with non-90° corners (common in old city centres) get their real angles instead of being forced to 90°.
  2. **Remove saw teeth:** drop jogs and zigzags smaller than a tolerance (default 30 mm, slider), Douglas–Peucker on what remains.
  3. **Fit lines:** group the remaining points into runs, fit each run with a least-squares line (robust to outliers: RANSAC or iterative trimming).
  4. **Snap angles:** lines within N degrees (default 5°, slider) of the main axes become exactly parallel or perpendicular to them; 45° optional.
  5. **Merge:** collinear neighbor lines become one; corners rebuilt as line intersections.
  6. **Regularize thickness (option):** two parallel faces of a wall are made exactly parallel and the thickness rounded (to 10 mm, or to a standard list: 50, 70, 100, 150, 200, 250, 300…).
- [x] Straighten tool and Wall › Straighten command (shortcut): works on the selection (click, Shift+click, box or lasso, or all walls of the level). Live preview: the straightened outline drawn over the original in another color, with the segment count before → after (e.g. "47 → 4 segments"). Tolerance and angle sliders in the options bar; Enter applies, Escape cancels. One history step for the whole selection. *(select with click/Shift/box; live green preview with 'N → M segments'; Enter applies, Esc cancels; no lasso)*
- [x] "Make rectangular" for a single straight wall: replace it by the best-fitting rectangle (oriented minimal-area rectangle on the main axis), i.e. one clean wall with a center line and a thickness.
- [x] Optional at the end of the import wizard: "Straighten all detected walls" checkbox with the same settings (on by default for scanned plans).
- [x] Adjacent walls keep touching: after straightening, endpoints within the tolerance of another wall's face are snapped onto it (no gaps or overlaps at T and L junctions).
- [x] Tests on synthetic walls: noisy straight wall → 4 segments; L-shaped wall with saw teeth → 6 segments; wall rotated 12° → straightened along 12°, not 0°; tolerance 0 → unchanged; determinism (pinned output).

**Validation (3e)**

- [x] On the scanned A4 and the phone photo plans, "Straighten all" reduces the segment count by at least 80 % and no wall moves by more than the tolerance; a few clicks fix the remaining crooked walls. *(automated on synthetic scans: ≥ 80 % fewer segments, no corner moves more than the tolerance; the real photos are a manual check)*

**Validation**

- [ ] Five real plans (clean CAD export, scanned A4, phone photo at an angle, hand drawing, the author's building): at the suggested settings, every load-bearing wall is detected and text/dimensions are dropped; record failures in `docs/detection-results.md`.
- [x] A 3-level building imported from 3 JPGs: after alignment, the stairwell walls are superposed within 50 mm. *(automated: three synthetic scans with different stretch and offsets)*
- [x] A plan scanned with a 3 % stretch in width only: 3-point alignment brings it within 50 mm of the level below; the side handle alone does the same by hand in under a minute. *(automated: 3 point alignment within 50 mm, and the side handle gives the 3 % stretch in one drag; the 'under a minute' part is a manual check)*
- [x] Detection of an A3 plan at 300 dpi takes a few seconds at most, with live preview, on each OS. *(automated: ~1 s for 4961×3508 px on this Mac; other OSes not measured)*

## Phase 4 — Plan view and walls by hand

- [x] `PlanView` control: background image, walls (filled polygons), grid, zoom around the mouse (⌘/Ctrl + wheel, pinch), pan (Space + drag, middle mouse). *(grid, walls, ghost of level below, zoom/pan; no background image yet)*
**Moving between levels**

- [x] Level bar: one button per level (name + altitude in the tooltip), current level highlighted; click to change level; Page Up / Page Down go one level up/down; the change keeps the zoom and the look-at point (`ViewState`). *(scrollable, tested with 30 levels; no right-click menu, no tooltip)*
- [x] Ghost of the level below drawn faded under the current level (on by default); ghost of the level above optional. Toggles in the View menu. *(menu toggles in the View menu; the 'above' ghost is a flag only, not drawn yet)*
- [x] Risers crossing the current level show a small up/down arrow; clicking it moves to the next level the riser reaches, keeping the riser centered.
- [x] Right-click on the level bar: add level above/below, rename, set altitude/height. *(add above/below, rename, altitude/height, level kind)*

**Input model (hybrid click/drag)**

- [x] `ToolStateMachine` base in Core: `Idle → Pressed → Dragging | Placing → Committed`, drag threshold in screen pixels passed by the view (default 4 px), so click mode and drag mode come for free in every tool. Tests for both paths and for the threshold.
- [x] Chained tools (Draw Wall, later Draw Pipe): each click adds a point and starts the next segment; double-click or Enter finishes; Backspace removes the last point; Escape cancels the segment in progress (a second Escape cancels the whole run); clicking the first point closes the outline.
- [x] Rubber band preview with live length and angle shown next to the cursor. *(length only, no angle)*
- [x] Typed values while placing: typing a number shows an input box near the cursor; Enter applies it as the segment length along the current direction (`2.4` = 2.4 m, `2400mm` accepted). Tab switches to the angle field. *(length, Tab → angle; Enter applies)*
- [x] Right-click while placing: context menu Finish / Undo last point / Close outline / Cancel.
- [x] Status bar hint per tool state (e.g. "Click the next point — Enter to finish — Esc to cancel"), translated. *(French only; English translation table pending)*

**Tools**

- [x] Tools in Core implementing `ITool` with `ToolPointer` in **mm** (no Avalonia types), unit-tested: Select (click, box by drag, Shift add), Move (drag, or click then click destination), Draw Wall (chained, thickness option), Draw Room (rectangle by drag or two clicks → 4 walls), Delete. *(Select with box + group move, Draw Wall chain; no Move-by-click, Draw Room, Delete tool)*
- [x] Snapping (`Snapper` in Core): grid, wall corners, wall faces, midpoints, orthogonal 0/90° (45° with a modifier), alignment with existing points (dotted guide line), and points of the ghost level. Snap indicator (small symbol per snap kind) drawn by the view from a `ToolOverlay`.
- [x] Measure tool: two clicks → distance shown, nothing added to the model.

**Doors, rooms and cellars**

- [x] Level kind: Basement/cellars, Ground floor, Standard floor, Attic, Roof, Site. A basement level defaults to `Common` stability and its spaces to "Cellar". *(set from the level context menu; defaults flow to spaces/walls)*
- [x] Door tool: click on a wall → door inserted at that position (default 800 mm for apartments, 700 mm for cellars, editable); the wall gets the opening (cut in 2D, hole in the 3D mesh); swing arc drawn in plan; click the door then the arc to flip the swing side; drag along the wall to move it, side handles to change the width. Windows and simple openings work the same way. *(Core `AddOpening`/`FlipOpening` done; no tool, no cut in 2D/3D)* *(click to place, click again to flip, drag to move; width option; no side handles)*
- [x] Space tool: click inside an area closed by walls → space created from its outline (flood fill on the walls polygon set, `SpaceFinder` in Core, tested), or drawn by hand. Name and number shown at the center of the space in plan. *(Core `SpaceFinder` flood fill done; no tool)* *(refuses rooms that are not closed, with a message)*
- [x] **Cellar numbering**: "Number cellars" command on a basement level: click the cellars in order (or let it number them along the corridor) → "Cave 1, 2, 3…" with a configurable prefix and start; the number is also put on each cellar's door. Renumber keeps one history step. *(Core action `NumberSpaces` done; no UI command)* *(Number tool: click cellars in order, Enter; prefix/start options; labels the doors)*
- [x] Display options: show/hide private interiors; private elements drawn lighter with a "may have changed" hint, common parts drawn solid. Filter in 2D and 3D: "Common parts only" (useful for the conseil syndical and for networks that run through common parts). *(Core filter `Spaces.CommonWalls/CommonSpaces`; no UI toggle)* *(Common parts only; ghost toggles; private elements are hidden, not drawn lighter)*
- [x] Spaces list panel: per level, kind, number, lot, area (computed), with a click to zoom on it; export as CSV (no owner names: lot numbers only). *(CSV export in Core `Spaces.ToCsv`; no panel)* *(Spaces tab with click-to-zoom; CSV export via Core `Spaces.ToCsv`, not in the menu yet)*
- [x] Networks know where they run: each segment gets the space(s) it crosses (computed), so the bill of materials and the network panel can say "riser in Cave 12" or "shut-off valve in the boiler room". *(`Spaces.CrossedBy`; not shown in panels yet)* *(Core `Spaces.CrossedBy`; shown nowhere in the UI yet)* *(Networks tab: "runs through: …")*
- [x] Properties panel: selected wall's thickness, height, label.

**Validation**

- [ ] Draw a 4-room flat by hand in under 5 minutes, by someone who has never used the app (usability check).
- [ ] Snapping tests pass on the 3 OSes (pure Core).
- [ ] On the author's basement plan: all cellars become numbered spaces with a door in under 15 minutes; "Common parts only" shows the basement, stairwell and shafts of every level.

## Phase 5 — 3D view

- [x] `ImmoCad.Scene`: `MeshBuilder` producing vertex/index buffers (float positions in metres, normals, color per network/category): wall extrusion from footprint polygon and height, floor slab per level (from the outer outline or a bounding polygon), pipe cylinders, fittings (torus segment for elbows, simple tee), equipment boxes. Deterministic; pinned checksum test. *(cylinders with normals, elbows as bent cylinders, tee/reducer as boxes, slabs under each level; no per-vertex colour buffer: colour is per object)*
- [x] `Picking`: ray vs meshes → id of the wall, equipment, node or segment under the mouse. Unit-tested.
- [x] `Viewport3D` control (Avalonia `OpenGlControlBase` + Silk.NET.OpenGL): orbit (left drag), pan (right or middle drag), zoom (wheel), "fit all", top/front/side/isometric presets. Simple lighting (one directional light + ambient). *(`GlViewport3D` written with Silk.NET; works on macOS, where the window asks Avalonia to render with OpenGL; falls back to the software viewport automatically — the GL path has not been run on Windows/Linux)*
- [x] Render modes: **Solid**, **X-ray** (walls translucent, networks opaque), **Wireframe** (wall edges + networks in color). `Scene` produces an edge list per mesh for wireframe.
- [x] Level filters: current level only / current + below / whole building; **cut above the current level** (dollhouse view) on by default; show/hide each network.
- [x] Level bar works in 3D too: changing level moves the cut and the camera target vertically.

**Switching 2D ↔ 3D without losing context**

- [x] One key (Tab) and the toolbar segmented control switch 2D / 3D / Wireframe; the choice is in `ViewState`.
- [x] Animated transition (~0.5 s, ease-in-out): the 3D camera starts as an orthographic top view matching the plan view's framing (same look-at point and scale), then tilts to the last used 3D angle (default isometric). Going back to 2D reverses it and restores the plan exactly where it was. Animation can be turned off in settings. *(toggle `AnimateTransitions` in settings; orthographic blend is geometry-only in the software viewport)*
- [x] Selection, current level and look-at point are shared: selecting in one view highlights in the other; "Show in 3D" / "Show in plan" on a selected object frames it. *(click a wall or pipe in 3D selects it and shows its level in the plan; no 'Show in 3D' command yet)*
- [x] Optional side-by-side mode (plan left, 3D right, synchronized through `ViewState`) for large screens. *(⫴ button; level and selection are shared, the 3D look-at is not synchronized)*
- [x] Tools work in both views: the 3D view converts the pointer to mm on the current level's plane (or on the constrained axis for handles) and feeds the same Core tools. *(riser, palette objects, doors, windows, rooms, numbering and measure by clicking the floor in 3D; chained wall/pipe drawing and handles stay in the plan)*
- [x] Headless tests: when OpenGL isn't available, render a fallback (wireframe with Avalonia drawing) so screenshots still show something; test the camera math in Scene.

**Validation**

- [ ] A 6-level building with all its walls orbits at 60 fps on an integrated GPU, on each OS.
- [x] Clicking a wall in 3D selects it in the plan view. *(automated headless test)*
- [x] Switching 2D → 3D → 2D ten times in a row: the plan comes back at exactly the same position and zoom; the transition is smooth on each OS. *(exactness automated; smoothness on each OS not checked)*
- [ ] A beginner finds a pipe crossing a slab between two levels using the level bar and X-ray mode without help.

## Phase 6 — Networks model

- [x] `NetworkType`: HeatingSupply, HeatingReturn, DomesticHotWater, ColdWater, Drainage, Gas (extensible), each with default color, default diameter and material.
- [x] `Network` = graph: `Node` (id, `PointMm3` position, optional attachment: wall face, equipment port) and `Segment` (node A, node B, outer diameter mm, material). Segments must be axis-aligned (X, Y or Z) in the MVP; 45° later.
- [x] `FittingResolver` (Core, pure): derives each node's fitting from its segments: end cap, straight joint, elbow 90°, tee, cross, reducer (diameter change), floor/slab crossing (vertical segment crossing a level's altitude), wall crossing. Fully unit-tested.
- [x] Validation rules (warnings, not errors): unconnected end, segment through a wall without crossing, two networks overlapping, slope missing on drainage.
- [x] `BuildingActions` for networks: add node/segment, move node, split segment, merge collinear segments, delete, change diameter/type. All in `HistoryTests.Actions`.
- [x] Networks panel: list of networks, color, visibility, lock.

**Validation**

- [x] `FittingResolver` tests cover every node configuration; the resolved fittings of a sample building are pinned.

## Phase 7 — Object palette, equipment and risers

**Palette**

- [x] Palette panel (left, collapsible) with categories: **Structure** (wall, partition, door, window, slab, stairs, service shaft), **Networks** (pipe, riser, riser templates), **Valves & fittings** (shut-off valve, thermostatic valve, balancing valve, air vent, check valve, pressure reducer, meter), **Equipment** (radiator, boiler, hot-water tank, manifold, circulator pump), **Sanitary** (washbasin, sink, WC, shower, bathtub). Search box, "Recent" section at the top, large icons with labels (beginner friendly). *(search + categories as list; not collapsible, no Recent section shown, no large icons)*
- [x] Two kinds of entries: **drawing tools** (wall, partition, pipe, riser: clicking the entry activates the tool) and **objects to drop** (everything else: drag onto the view, or click the entry then click in the view, using the hybrid input). *(click entry then click in view; no drag-and-drop)*
- [x] Object catalog as JSON in resources (`catalog/*.json`, source-generated deserialization): id, category, name (translated), parameters with defaults (width, height, diameter…), **2D symbol** (simple vector path, standard plumbing/heating symbols), **3D shape** (combination of boxes and cylinders built by `ImmoCad.Scene`), typed **ports** (network type + diameter + position/direction). Catalog test: every entry loads, has a symbol, a shape and valid ports. *(17 entries in `catalog/*.json`; names fr/en; parameters, 2D symbol paths, 3D boxes/cylinders, typed ports; test loads every entry)*
- [x] Drop rules (`PlacementRules` in Core, unit-tested): doors/windows snap into walls; radiators and sanitary objects snap against a wall face at a default height (radiator 150 mm above floor), oriented along the wall; **inline components (valves, meters, pumps) only drop onto a pipe**: they snap to it, split the segment, align with its axis and take its diameter. In the network graph an inline component is a **node with a `Component`**; it is counted in the bill of materials. *(valves/meters snap onto pipes and split them; radiators snap to wall faces, default height from the catalog)*
- [x] Preview while dragging: ghost of the object, green when the placement is valid, red with a hint when not ("A valve must be placed on a pipe"). *(ghost follows the cursor; green/red + hint)*
- [x] User objects (later): duplicate a catalog entry, change its parameters, save it in the user's catalog folder. *(Core: duplicate, change parameters, save/load in a folder; no UI to create them yet)*

**Equipment**

- [x] Equipment model: instance of a catalog entry with its parameter values, position, level, orientation, host (wall face, pipe) and ports.
- [x] **Riser (colonne)** is not a separate type: it is a set of vertical segments of one network. The Riser tool creates it (see Phase 8a).
- [x] Connect: drag from a radiator port to a riser or a pipe → creates the connection segments with orthogonal routing (down to a chosen height, along the wall, to the riser). One history step. *(Core `ConnectOrthogonal`; no drag UI)*
- [x] Properties panel: equipment dimensions, ports, level, label (e.g. "Radiateur séjour").

**Validation**

- [ ] On the author's building: place the radiators of one flat and connect them to the existing heating risers in under 10 minutes.

## Phase 8 — Pipe tools (the core of the UX)

**8a — Risers from the top view (circles)**

- [x] Riser tool in the plan view: click to place a circle = a vertical pipe at that XY. Options bar: network type, diameter, **from level / to level** (defaults: whole building).
- [x] Templates: place 2 or 3 risers at once with fixed spacing (e.g. hot + cold water, heating supply + return, or hot + cold + drainage), editable spacing and order. Saved as user templates. *(Core `AddRiserTemplate`; no UI, no saved user templates)* *(spacing editable; saving user templates is not wired to the UI yet)*
- [x] Riser snaps to wall corners and faces with an offset (default: 50 mm from each face).

**8b — Pipe on a wall corner from the elevation view**

- [x] Elevation view: pick a wall (or two walls meeting at a corner) and see it from the front, with level lines and altitudes. *(longest face of the selected wall; no two-wall corner picking)*
- [x] In the elevation view, click the corner (or a point on the face) → vertical pipe attached to that corner, offset from both faces; drag to set its top and bottom, snapping to level altitudes. *(click = whole building, drag = top/bottom snapped to level lines; offset 50 mm from the face)*
- [x] Horizontal pipe along the wall face at a given height above the floor.

**8c — Extent over several levels**

- [x] Every vertical run has an editable extent "from level X to level Y" (Properties panel) and top/bottom handles in elevation and 3D views that snap to level altitudes; slab crossings are added automatically. *(Core `SetRiserExtent`; no Properties/handle UI yet)* *(Properties: from/to level; elevation view: top/bottom handles snap to level lines; no 3D handles)*

**8d — Direct manipulation with handles**

- [x] Draw Pipe tool: chained click mode like Draw Wall (each click = a new segment and an elbow), with the same Enter/Backspace/Escape/typed-length behavior; segments constrained to X/Y in plan, Z in elevation. *(plan view only: X/Y constrained by orthogonal snap, height from options)*
- [x] Handles use the hybrid input: drag the handle (Paint style), or click the handle then click the destination (AutoCAD style).
- [x] End handle: drag to **extend** along the segment axis; the length is shown live; snapping to walls, other pipes and level altitudes.
- [x] End handle + modifier (Ctrl / ⌥ on macOS) or drag perpendicular past a threshold: **create an elbow** and a new segment along the new axis (X, Y or Z); keep going to chain elbows.
- [x] Middle of a segment: drag to **create a tee** and a branch.
- [x] Move a whole run perpendicular to itself (keeps connections by stretching the connected segments).
- [x] Type a length while dragging (e.g. "1.25" Enter) for precision.
- [x] Each gesture = one history step. The tool previews by quiet model changes and commits on pointer-up (same pattern as CinnabarSharp's editable tools).
- [x] The same handles work in the plan, elevation and 3D views (3D: drag constrained to the axis under the mouse, axis gizmo shown). *(plan: all three handles; elevation: top/bottom of vertical pipes; 3D: pipe ends, drag locked to the pipe axis with a dashed gizmo and the live length — drawn by the software viewport, the OpenGL viewport has no overlay yet)*

**Validation**

- [ ] A beginner lays the heating supply/return of a 3-level building from scratch (risers + one flat's radiators) without help, in under 20 minutes; list friction points.
- [x] Tool tests in Core cover extend, elbow, tee, move-run, typed length, snapping to level altitudes.

## Phase 9 — Bill of materials and exports

- [x] Bill of materials: per network, length per diameter and material (rounded up to standard bar lengths, option), count of each fitting type, list of equipment. View in a panel, export to CSV and to a printable PDF. *(partial: pipe length per network/diameter + CSV; no fittings/PDF/bar lengths)*
**Exports to other tools** (File › Export, one exporter per format behind `IBuildingExporter` in Core, registered in DI like CinnabarSharp's formats; each has a round-trip or golden-file test)

- [x] **IFC 4** (`.ifc`), the BIM standard, priority export: building → storeys (`IfcBuildingStorey` per level) → `IfcWall` (extruded footprints), `IfcSlab`, `IfcDoor`/`IfcWindow`, networks as `IfcDistributionSystem` with `IfcPipeSegment`, `IfcPipeFitting` (elbows, tees from `FittingResolver`), `IfcValve`, `IfcSpaceHeater`, `IfcBoiler`…, colors per network, geographic placement (`IfcSite` with the Lambert 93 anchor once Phase 12 exists). Opens in FreeCAD (BIM workbench), Revit, ArchiCAD, BlenderBIM/Bonsai, online IFC viewers. Library: evaluate xBIM Essentials and GeometryGym IFC (check licenses and AOT compatibility before choosing; write an ADR in `docs/adr/`). *(partial: hierarchy, walls, pipe segments only; no geometry/fittings/colors)*
- [x] **FreeCAD macro** (`.FCMacro` / `.py`): a generated Python script that rebuilds the building as **native, editable FreeCAD objects** (BIM/Arch walls on levels, pipes as swept profiles or BIM pipes, equipment as parametric boxes, one group per network with its color). Best fidelity for FreeCAD users; no FreeCAD dependency at build time (text generation only).
- [x] **DXF** (`.dxf`): 2D plans per level, one layer per category and per network (AutoCAD, LibreCAD, DraftSight, QCAD). Library: netDxf (check license) or a minimal hand-written DXF R12 writer. *(partial: LINE entities per layer; no R12 header/units)*
- [x] **glTF** (`.glb`) and **OBJ**: 3D meshes from `ImmoCad.Scene` for Blender, SketchUp, web viewers, 3D printing; one node per level and per network so they can be hidden in the target tool. *(OBJ + GLB in Core/MCP; no UI menu entry for GLB yet)*
- [x] **SVG / PDF**: printable plans per level and per network (title block with level name, scale, north arrow, legend). *(SVG per level in Core; PDF only for the bill of materials; no title block / north arrow)*
- [x] **CSV / PDF**: bill of materials (above).
- [x] Export dialog: format, levels to include, networks to include, units (mm/m), with a short line explaining which tool each format is for (beginner friendly). *(File › Export…; quick-export entries kept)*
- [x] MCP tools `export_ifc`, `export_freecad_macro`, `export_dxf`, `export_gltf` (Phase 10).

**Imports from other tools (later)**

- [x] Reference model: import a glTF/OBJ/IFC (e.g. the author's existing FreeCAD model) as a locked, non-editable reference mesh to trace over, per level. *(OBJ and GLB, locked, drawn as a section 1 m above the floor of the level; IFC meshes are not read as references, use the IFC import)*
- [x] IFC import of walls and storeys (editable), once the IFC export is stable.

**Validation**

- [x] The bill of materials of the sample building matches a hand count. *(automated hand-count test on a simple run and on the sample)*
- [ ] The IFC of the sample building opens in FreeCAD and in an online IFC viewer: storeys, walls, pipes, fittings and valves are present with the right types, positions and colors.
- [ ] The FreeCAD macro runs in FreeCAD 1.x without errors and produces editable walls and pipes at the right scale. *(syntax checked with Python in a test; not run inside FreeCAD)*
- [ ] The DXF opens in LibreCAD/QCAD at the right scale with one layer per network; the glTF opens in Blender and a browser viewer.

## Phase 10 — MCP server

- [x] `ImmoCad.Mcp` (official C# SDK), Core only, hosted by the app: `ImmoCad --mcp` (headless, stdio) and `--mcp --attach` (relay to the running app over a Unix domain socket, File › Allow AI Agents, off by default). Copy the design from `CinnabarSharp.Mcp` (dispatcher, `McpContext.Run`, allowed folders, `overwrite`/`discardChanges`). *(partial: stdio only; no `--attach`)*
- [x] Tools: open/new/save/close building, list levels, add level, import plan with detection parameters, list walls, add riser (network, XY, level range), add pipe run (list of points), connect equipment, bill of materials, export glTF/SVG/CSV, undo/redo. *(partial: add level/wall/riser/pipe run, save, export, undo/redo, BOM; no open/import)*
- [x] Resources: `immocad://buildings`, `immocad://buildings/{id}/networks`, `immocad://equipment-library`.
- [x] `docs/mcp.md` with `claude mcp add` setup and examples ("add a hot and cold water riser from RDC to R+5 in the corner of the bathroom").
- [x] `ImmoCad.Mcp.Tests`: start the real app with `--mcp` and drive it end to end.

**Validation**

- [x] Claude Code builds a 2-level building from two JPGs and adds risers through MCP on each OS. *(automated through the tool classes and over stdio on this Mac; Windows and Linux not run)*

## Phase 11 — Polish and packaging

- [x] Settings persistence (JSON in the per-OS app-data folder): window, panels, last tool, snapping options, templates. *(window size, recent files; panels/last tool/snapping options not persisted yet)*
- [x] App icon, About dialog, light/dark themes (design tokens like CinnabarSharp's `Themes/`). *(generated icon in all sizes + .ico; About dialog; design tokens in `Themes/Tokens.axaml` with Light/Dark; Auto/Light/Dark in the View menu, persisted)*
- [x] `packaging/package.sh <rid> <version>`: self-contained, Native AOT, Windows zip, macOS `.app`/`.dmg` (single architecture), Linux tarball with `.desktop` file; `.immeuble` file association.
- [x] Release workflow: pushing a `v*` tag packages win-x64, linux-x64, osx-arm64, osx-x64 and publishes a GitHub release.
- [x] Unsaved work recovery (autosave every few minutes to the app-data folder, offered at next start after a crash); the crash itself is already in the log (Phase 1). *(autosave every 3 min, recovery after a crash)*
- [x] Onboarding: a sample building opened from the welcome screen, and a 5-step guided tour (import a plan, calibrate, detect, add a riser, extend a pipe). *(sample from the welcome screen; 5-step tour offered on first start and in Help)*

**Validation**

- [ ] Download the release packages on fresh machines of each OS: the app starts, imports a plan, saves and reopens a building.

## Phase 12 — Site, map and cadastre (after the MVP)

Place the building on its real plot, using French open data (IGN Géoplateforme, Licence Ouverte 2.0).

- [x] Georeferencing in the model: `Building.Site` = anchor point in **Lambert 93 (EPSG:2154, metric)** + rotation (true north) + optional altitude. Model coordinates stay in mm relative to the anchor; conversions WGS84 ↔ Lambert 93 only at the import/export boundary (`Lambert93` helper in Core, pure C#, tested against reference points).
- [x] Interfaces in Core: `IGeocoder` (address → coordinates), `ICadastreService` (parcel and building footprints around a point, GeoJSON), `IMapTileSource` (WMTS tiles: orthophoto, IGN plan, cadastral parcels). Implementations in a separate `ImmoCad.Geo` project (HttpClient, no UI); fakes in tests. Tiles and GeoJSON cached on disk so the app works offline once loaded. *(Geo project with disk cache; no HTTP integration test against the real service in CI)*
- [x] **Site level** in the level bar, below the lowest level: shows the chosen background (orthophoto, IGN plan or cadastre) under the building footprint, with parcel boundaries and cadastral buildings as vector outlines. *(map tiles, cadastral buildings, the building through its anchor, attribution; pan with the left button, wheel zooms)*
- [x] Place the building: type an address → zoom on the plot → click two corners of the building on the plan and the matching corners of the cadastral footprint → anchor and rotation computed. One history step. Or pick the cadastral footprint directly to start a new building from it (outer walls pre-drawn).
**Cadastre-assisted geometry (the cadastral footprint as a guide for angles and dimensions)**

- [x] Once the building is placed, the cadastral footprint (PCI `batiment` layer) becomes a **reference outline** for every level: shown as a dashed overlay in the plan view, togglable.
- [x] Reference angles: the footprint's edge directions feed `ReferenceGeometry`, so Straighten (3e) snaps outer walls to the **real angles of the building** (e.g. 84° or 97° corners) instead of 90°; interior walls still snap to the main axes.
- [x] Snap outer walls to the footprint: the Snapper offers footprint edges and corners; a "Fit outer walls to cadastre" command moves the outer face of façade walls onto the footprint edges when they are within a tolerance (default 300 mm), with preview and one history step. *("Fit outer walls to cadastre" with one history step, and the snapper offers the footprint's corners and midpoints)*
- [x] Check the plan's scale and stretch: affine fit of the detected outer outline onto the footprint → suggested scale X / scale Y / rotation, shown as "your plan is 2.8 % wider than the cadastre — apply?". Feeds the alignment of Phase 3c (the footprint acts as a common reference for all levels, so each level can be aligned on it directly). *(applies a scale correction to walls and background; rotation is reported, not applied)*
- [x] Angle check: list the corners whose angle differs from the footprint by more than N degrees (default 2°), with a click to zoom on each.
- [x] Shared walls (mitoyenneté): footprint edges shared with a neighbouring cadastral building are marked, so the user knows these walls are party walls.
- [x] The cadastre is a **guide, not the truth**: its precision varies between communes and footprints can merge adjoining buildings or omit recent extensions. Every cadastre-based correction is a suggestion with preview, never applied silently; the user's plan wins when they disagree.
- [x] `FootprintMatcher` (Plan, pure): match plan outline edges to footprint edges (direction + distance), robust to missing or extra edges; tests on synthetic footprints with rotation, stretch, a missing edge and a merged neighbour.
- [x] North arrow in the plan view and in exports; plan view can be shown "north up" or "building aligned". *(View › North up / building aligned; arrow in the plan, the map and the SVG)*
- [ ] Connections from the street (later): water, gas, sewer entry points on the plot boundary, distances to boundaries.
- [x] IFC export uses the anchor for `IfcSite` geographic placement.
- [x] Attribution of the data sources shown on the map (IGN, DGFiP) and in exports.

**Sites made of several buildings and parcels (e.g. two cadastral buildings, the space between them, and car parks underneath)**

- [ ] Multi-footprint selection: Shift+click adds cadastral buildings to the site; they are merged into one reference outline, so snapping, scale/angle checks and "Fit outer walls to cadastre" work on the whole.
- [ ] Parcel boundaries (not only buildings) are fetched and drawn in the plan as a dashed reference, with a warning when a wall goes outside the parcel(s); useful for car parks that extend beyond the building footprint.
- [ ] One reference outline per level: ground floor = buildings + the space between them; basement = the car-park shape inside the parcel limits; the current level shows its own reference.
- [ ] Map as an underlay of the plan (cadastre / aerial photo, with a toggle), so a level can be drawn straight on the map when no plan exists.
- [ ] Guided placement: address → footprints/parcels → plan → two matching points on the plan and on the map (rotation, scale and position computed at once) → checks, with the result shown at each step.
- [ ] Party walls: edges shared with a neighbouring building are marked automatically as party walls (thickness and stability set), for the bill of materials and co-ownership.

**Validation**

- [ ] The author's building placed from its address: the plan matches the cadastral footprint within 0.5 m.
- [x] On a building with non-90° corners, Straighten with the cadastre reference keeps the real corner angles (within 1°), and the scale/stretch suggestion matches a manual calibration within 1 %. *(automated: 96° corner kept within 1°, scale suggestion exact on a synthetic 2.8 % stretch)*
- [x] With the network off, a building opened before still shows its site from the cache. *(automated at the HTTP-cache level)*

## Phase 13 — Walk mode (first-person inspection, after the MVP)

Walk through the corridors of each level, at eye height, to see the pipes and objects laid out where they really are. It is an **inspection** view, not a game: nobody should need gaming habits, get stuck or get lost.

**Design decisions**

- **Street View model first, keyboard second.** Drag = look around; click on the floor = glide there (a disc on the floor shows the destination); click on an object = select it. Arrows and WASD also work. The mouse pointer is never captured: it stays free to point at pipes, and Avalonia has no pointer lock anyway.
- **"You are here" = a flat minimap of the current level, not a small 3D building.** A 3D building in a corner shows the floor but not the corridor (its walls hide the marker at 200 px) and repeats what the level bar already says. So: top right, the plan of the current level, same orientation as the plan view, with a position marker and a view cone; click = jump there. The level bar stays visible and answers "which floor". The minimap replaces the ViewCube, which has no use in this mode.
- **Whole-building overview = leaving the mode.** Esc goes back to the previous view (plan or 3D), framed on where you were, with a pin at the last position. No second renderer in the corner.
- **The building is a guide, not a hard world limit.** Traced plans have gaps and missing doors, so strict collisions would leak or lock users in. Only wall material blocks, with sliding, from both sides, at the height of the eye: any opening is a passage, whatever its kind; pipes and objects never block. A hole in a wall is a passage both ways: a gap in an outer wall leads outside, where the user floats at the same height, and the way back in is through a hole. The world limit is the last stop, and two escapes are always available: minimap jump and ghost mode.
- **Floors change like an elevator.** The model has no stairs. The level bar and Page Up/Down keep the XY position (or the nearest walkable point), with a short fade. Walking on stairs is out of scope for this phase.
- **Read-only.** Moving changes nothing in the building: no history step, no dirty flag. Editing stays in the other views.

**13a — Prerequisites: a building you can walk through**

- [x] Floors and ceilings for walking: flat rectangles over the bounding box of the level's walls (with a small margin), at the level's altitude and at altitude + height. `SceneLevel` already has both values. Walls hide the overflow from the inside; it only shows through windows and gaps, which is acceptable. *(flat boxes over the bounding box of the level's walls; picking ignores them)*
- [x] A gap between walls is already a passage: walking needs no opening data there. Check it on a traced plan (doors are usually gaps) before anything else in this phase. *(checked on the author's plan-126: 0 openings and 0 rooms, yet about 99 % of the free floor of each upper level is connected, and the facade gaps lead outside; the basements' centre fell in a closed 7 m² pocket, so the automatic start now picks the largest free region instead)*
- [x] Doors and windows placed with the tools: `Wall.Openings` never reach `SceneMapper`, so the wall stays solid at these places (only these places). Cut per height band (below the sill, opening, above the lintel), using `PolygonOps.Subtract` on the footprint for the opening band, so lintels and sills stay. A wall without openings keeps exactly the same mesh (pinned checksums unchanged). *(`SceneMapper` cuts the wall in bands at sills and tops with `PolygonOps.Subtract`; a wall without openings keeps its single extrusion (checksums unchanged); walls whose centre line is undefined are not cut, like in the plan)*
- [ ] Opening rectangle in Core, shared by plan, mesh and collisions: move the geometry out of `PlanView.DrawOpenings`, and handle walls where `WallGeometry.Axis` is null or merged wall groups (the opening is placed on the nearest wall section, not on the axis of the whole group). *(`WallGeometry.SpanOf` is shared by the 3D mesh and the collisions; `PlanView.DrawOpenings` still has its own copy)*
- [ ] Door frames, with the door number or space label shown beside the door (`Cave 12`). No door leaf and no glass: an opening is a hole, and nothing in front of it hides the pipes. *(not done)*
- [ ] Sample building: add a corridor, rooms, doors and pipes along the corridor, so the mode can be tried immediately and tested. *(not done: the sample is pinned by many tests (bill of materials, IFC counts), so it stays as it is)*

**13b — Perspective camera and rendering**

- [x] `WalkCamera` in `ImmoCad.Scene` (pure, BCL): eye position, yaw, pitch clamped to ±85°, vertical FOV 70° (30–80° with the wheel), near plane 5 cm. `Project`/`Unproject` tested. Same yaw/pitch maths and same depth along the view axis as `OrbitCamera`, with x and y divided by that depth.
- [x] `ViewportController` works with either camera: picking casts from the eye; orbit, pan, handles and `ViewCube` are off in walk mode. *(drag = look, wheel = field of view, click = select or go, hover = label)*
- [x] `GlViewport3D`: the vertex shader already computes the depth along the view axis; in walk mode it becomes `gl_Position.w`, with `z` mapped between the near and far planes. OpenGL then does the near-plane cut and the per-pixel depth test. The orbit view keeps `w = 1` and stays identical. *(checked on macOS (OpenGL 4.1 core))*
- [x] Legible indoors: one flat grey in a corridor reads as a blank screen. Light attached to the camera with distance falloff, distinct floor / wall / ceiling colours, dark edge lines on walls (`WireEdges`). *(light at the eye with falloff, three tones, dark wall edges with a polygon offset)*
- [x] Walk scene: current level only, plus pipes and fittings crossing its height band; AABB culling per object; continuous frames only while moving. *(pipes are clipped to the band plus the slab margin, so a riser through the whole building no longer shows as endless lines from outside; a wire grid in the planes of the floor and the ceiling, hidden indoors, fills the white void outside the walls; both covered by Scene tests but not yet looked at in the running application)* *(no AABB culling yet)*
- [x] OpenGL only, no software fallback: `Viewport3D` does not draw this mode. Without OpenGL (`IMMOCAD_SOFTWARE=1`, failed context) the Walk entries are disabled with a translated explanation. If OpenGL fails while walking (`SwapToSoftware`), the app returns to the previous view with a status message. *(entries disabled with the reason; the return to the previous view when OpenGL fails while walking is coded but not exercised)*

**13c — Walkable area and movement**

- [x] `WalkArea` (Core, pure, in mm): the walls at the height of the eye, minus the openings of any kind that are open at this height (sill ≤ eye ≤ sill + height). The kind of opening plays no part: only the presence of wall matters. `Move(from, delta)` slides along walls with a 200 mm radius, so that a 700 mm cellar door can be crossed. Tests: slide, corner, door, window with the eye above and below its sill, eye above a lintel, narrow gap, outer limit, no tunnelling at high speed.
- [x] Nearest walkable point, for entry, level change and minimap jumps: inside the level's outer outline when the user was inside the building or clicked inside it, and on the side of the wall the user came from when both sides are at a similar distance. A jump into a closed room is allowed. Tests: arrival in a wall, in a thick wall, outside the outline of a smaller level. *(rings of 50 mm, preferring the side the user came from; going to a smaller level keeps a user who was inside the building inside its walls' bounding box, while someone floating outside stays outside)*
- [x] Level change: keeps the view direction; a status message says when the position was shifted ("Moved 0.4 m: wall at this position"). The requested XY is remembered until the next move, so going up then down returns exactly to the start. A level without walls is refused with a message; the user stays where they are. In ghost mode the position is kept as is.
- [x] Input loop with time step: 1.4 m/s, Shift ×2.5, short ease-in and ease-out, no head bob; eye at floor + 1 600 mm by default. Pressed keys are cleared when the window loses focus (no runaway walking). *(a key pressed and released between two frames still counts for one frame)*
- [x] Physical keys (`e.PhysicalKey`), so WASD stays in the same place on an AZERTY keyboard, plus arrows. In walk mode the tool shortcuts of `OnWindowKey` (W = Draw Wall, V, Delete…) and Tab are disabled; Esc leaves the mode. *(handled in the tunnel phase, so they work whichever control has the focus)*
- [x] Eye height: physical keys E (up) and Q (down, A on AZERTY), at 0.5 m/s, kept between floor + 200 mm and ceiling − 100 mm, shown in the heads-up line while it differs from the default. Back to 1 600 mm on a level change or with Home.
- [x] Click to go: floor point under the cursor with a disc preview, straight glide with sliding (what is visible is reachable, so no pathfinding), stopped by any key or drag. *(glide stops against walls; no destination disc yet)*
- [x] **Ghost mode** toggle (key and on-screen button), remembered in the settings because following a pipe or a service shaft is the main use: no wall collisions, only the world limit stops the user, and the user may stay inside a wall. Leaving ghost mode outside the building keeps the position: the user floats there at the same height, with collisions active. Leaving it inside a wall moves to the nearest walkable point, with the short fade and a status message; the view direction is kept. An eye inside a slab is brought back within the current level's height range. Tests: from a wall, from outside, from a slab, then coming back in through a hole. *(key X and the Ghost button)*
- [x] World limit, with or without collisions, automatic by default: bounding box of the walls, pipes and equipment of all levels, plus 1 m, so a pipe modelled out to the street can be followed with nothing to set up. Tests: empty building, pipe outside the walls, frame smaller than the building.
- [ ] Optional **World frame**: `Building.WorldFrame`, a nullable axis-aligned rectangle in mm, for one building rather than per level, replacing the automatic limit. Absent from older files = automatic (schema compatible). `BuildingActions.SetWorldFrame` (one history step, in `HistoryTests.Actions`). In the plan view: draw by drag, corner and side handles, dashed outline shown only when a frame exists or its tool is active, also shown on the minimap. It limits movement only: nothing is clipped from the rendering. *(model, history step, file format, dashed outline in the plan and in the minimap are done; instead of drawing it with handles, View › "Walk limits: use what the plan shows" takes the visible part of the plan, and "back to automatic" clears it)*
- [x] Following a riser in ghost mode: keys E and Q are no longer stopped by the floor and ceiling; the eye crosses the slab and the current level follows the eye's altitude (level bar, minimap and scene update), between the bottom of the lowest level and the top of the highest one. *(logic tested in Core; checked by hand on the sample (the dissolve and the dimmed risers))*

**13d — Entering, leaving, finding your way**

- [x] `ViewMode.Walk`, toolbar button next to 2D / 3D, View menu, command palette, a free shortcut. Not part of the Tab cycle. Available at every step of the guided path. *(shortcut G, Walk button in the toolbar, View menu (hence the command palette))*
- [x] Choosing the start: right-click "Walk from here" in the plan and in 3D, and "Walk" on a row of the Spaces panel. The toolbar button starts from the last position on this level, otherwise from the centre of the largest corridor, otherwise from the nearest walkable point to the plan's look-at; first look direction along the longest free line. *(right-click on the plan (Select tool) and the Walk button on each room of the Spaces panel; not from the 3D view)*
- [ ] Transition: animated descent from the current framing to eye height (off with `AnimateTransitions`). Esc restores the previous view and centres it on the last position through `ViewState.LookAt`. *(Esc restores the plan centred on the last position (with a pin); no animation)*
- [x] `MiniMapView` over the view (replaces `ViewCube` in this mode): walls, spaces, visible networks, marker with view cone; click to jump, drag on the cone to turn, button to collapse. Light control, not a `PlanView` instance. It must stay readable on a narrow side-by-side view (hidden under a minimum width). *(no drag-to-turn on the cone, no automatic hiding on narrow views (a button hides it))*
- [x] Heads-up line: level and current space ("R+1 — Corridor"), using `PolygonOps.Contains` on `Level.Spaces`.
- [x] Hide the tool strip and palette in this mode; keep the level bar, the Networks and Properties panels.
- [x] First-use hint, dismissible, shown once ("Drag to look · Click the floor to walk · Esc to leave"). All new text through `Translations` (`vm:L.*`).

**13e — Inspecting networks (the reason for this mode)**

- [x] Hover: outline on the pipe or object and a label under the cursor (network, diameter, material; for equipment its name and label). Thin pipes get a few pixels of tolerance around the ray. *(label next to the cursor and an orange highlight; no extra tolerance for thin pipes yet)*
- [ ] Click: same selection as the other views (`Pick3D`), with the Properties panel. "Show in plan" and "Show in 3D" from the selection actions. *(walls, pipes and equipment are selected with the Properties panel; no "Show in plan" button in the selection bar)*
- [x] **Seeing in ghost mode** (one mode with the toggle of 13c, no separate button): walls dissolve around the eye, fully within about 0.5 m and not at all beyond about 2 m, with a dither `discard` in the fragment shader (depth writes stay on, so no sorting). Distant walls stay opaque: X-ray at 28 % on everything is unreadable up close. Networks and equipment hidden by a wall, floor or ceiling are drawn dimmed on top (second pass with reversed depth test), so that pipes in partitions and slabs can be followed. *(dither dissolve between 0.5 and 2 m; hidden pipes and equipment dimmed on top (a visible pipe's far half is drawn dimmed over it too, barely noticeable))*
- [x] Network visibility and lock from the Networks panel apply live; small colour legend over the view.
- [x] **Room names** in the walk view: a floating plate under the ceiling at the middle of each room (a point near each end of a long corridor), with a dot of the room kind's colour. A name typed by the user is shown as is; a generated one ("Room 3") gives way to the kind ("Bike room") when the kind is not Room; the number is added unless already there. Only for rooms in front of the eyes, within 15 m (fading from 8 m), not behind a wall (ghost mode sees through), nearest first, without overlapping plates, never the room you stand in. Key R and a Names button, remembered in the settings, on by default. The map also shows the names that fit and lights the current room. *(`WalkLabels` and `WalkArea.LineOfSight` in Core, `RoomLabelsView` in Desktop; tested in Core and headless, the OpenGL rendering of the plates has not been looked at in the running application)*

**Later (not in this phase)**

- [ ] Stairs in the model and walking on them; door leaves and window glass as visual details; saved viewpoints; measuring and editing handles in walk mode; follow-a-pipe navigation; VR.

**Validation**

- [x] Unit tests pass for `WalkCamera` and `WalkArea`; pinned meshes of walls without openings are unchanged.
- [x] Headless tests (no OpenGL, so no walk image): mesh of a wall with a door, floor and ceiling rectangles, Walk entries disabled without OpenGL, minimap and heads-up line alone, in both languages. `LanguageTests` pass. *(including a screenshot of the overlay and the map in both languages)*
- [ ] Manual check on each OS, with OpenGL: corridor with a door cut out, floor, ceiling and pipes render correctly. *(done on macOS (Apple M5, OpenGL 4.1): walls, floor, ceiling, risers, minimap, ghost mode; Windows and Linux not run)*
- [x] Sample building: enter from the plan, cross a door, change level, press Esc, and the plan is centred on the last position; the document is still clean (no dirty flag). *(automated; the sample has no door, the door crossing is covered in Core tests)*
- [ ] AZERTY and QWERTY: same physical keys; no tool is activated while walking. *(physical keys used and no tool shortcut fires while walking; only one keyboard layout tried)*
- [ ] On a 6-level building with its pipes: 60 fps on an integrated GPU, on each OS.
- [x] On a real traced plan with gaps and missing doors: the user is never locked in, and someone who left through a gap finds the way back in. *(plan-126: walked in the SS2 basement and on RDC with OpenGL; connectivity checked by a flood fill over every level; that analysis was a scratch test and is not kept)*
- [ ] A beginner, without help: finds a given valve in a corridor and says which floor and room they are in, in under 2 minutes. List friction points.

## Phase 14 — Floor markings (parking lines that the scan made walls)

Painted lines on a parking floor are traced as thin walls: they block the walk, cut the rooms and clutter the plan. A **floor marking** is paint: a flat white shape on a level, never a wall.

- [x] Model: `Level.Markings` (`FloorMarking`: outline and holes, mm), saved in the file (old files have none), `BuildingActions` `ConvertWallsToMarkings`, `PaintMarkings`, `MarkingsToWalls`, `EraseMarkings` (one history step each, undo/redo exact).
- [x] `PolygonOps.SplitThin`: material thinner than a thickness (default 150 mm) is split off a wall by a morphological opening on a raster, optionally inside a zone; small blocks left at junctions (corners of a bay) go with the paint, a real pillar stays a wall. `FloorMarkings.PlanPaint` plans it for a level, walls in parallel. *(on the author's plan: ~0.9 s for a whole basement; 311 shapes at 150 mm on SS1, far fewer at 100 mm)*
- [x] Tool **Paint on the floor** (B, tool strip, Tools menu, command palette): drag a rectangle (or a freehand zone), an orange preview shows what would change and how many parts, Enter applies, Esc forgets the zone. Options: mode (thin walls → markings / markings → walls / erase markings), thickness, freehand zone. Nothing changes before Enter.
- [x] Selection action **To floor marking** for whole walls already separate (click, box, Shift).
- [x] Import wizard option "Thin lines become floor markings (parking bays)" with its threshold, in the same single history step as the walls (off by default: thin partitions of an apartment plan are walls).
- [x] Drawing: white with a thin grey outline in the plan, under the walls; flat 3 mm paint in the 3D views and when walking; DXF layer `MARKINGS_<level>`; SVG paint under the walls.
- [x] Markings are not walls: they block nothing in the walk, do not cut the rooms of the room tool, are ignored by the bill of materials.
- [x] A floor marking is selected like a wall: click (a few pixels beside a thin line is enough), Shift+click, box (walls and paint together). Selected paint shows in orange; the selection bar offers **Back to wall** and Delete; it moves with the walls (also up or down a level); one history step each.
- [x] The **cut tool** (line or zone) cuts the paint as well as the walls, and only the selected walls and paint when there is a selection; the pieces of a cut marking stay markings.
- [ ] Not done: IFC and FreeCAD exports ignore markings; "Copy to all levels", copy and paste, and the MCP tools do not know them; the paint is not hidden by the "Common parts only" filter.
- [ ] Not looked at in the running application: the OpenGL rendering of the paint (checked in Scene tests and in the headless plan, on the author's plan too).

**Validation**

- [x] On `plan-126` (SS1, whole level, 150 mm): the bay lines turn into paint, the thick walls and the cellar partitions stay walls, no black dots at the bay corners (headless render checked by eye).
- [ ] A beginner paints the bays of a parking level without help, in under 5 minutes.

## Cross-cutting — Language

- [x] **One language at a time.** The whole UI is English by default (menus, tool tips, panels, dialogs, status and hint lines, wizard, errors, exported text such as bill-of-materials headings); French is a complete second language; never both in the same screen. The language is switchable at runtime (View › Language, persisted in the settings), without restarting. A test fails when a key is missing in one language or a user-visible string is a hard-coded literal. *(English default, French complete, 470 keys; `vm:L.*` attached properties for XAML (no reflection bindings); View › Language switches at runtime and is persisted; generated names (levels, rooms, cellars, sample) are language tokens, names typed by the user stay as typed; MCP tool descriptions and log lines stay English, they are not UI. Tests: key parity, no French letters in English text, no hard-coded literals in views/view-models, whole window text scanned in both languages.)*

## Criteria for the MVP (0.1)

- [x] Import JPG plans for several levels, calibrate, detect walls, align levels. *(functionally complete, automated; not validated on the 3 OSes)*
- [x] 3D view of the building with level visibility and X-ray mode. *(functionally complete, automated; GL path unverified)*
- [x] Radiators from the library, risers from the top view (with templates), pipes on wall corners from the elevation view, level extents, extend/elbow/tee with handles. *(functionally complete, automated)*
- [x] Object palette with valves dropped onto pipes.
- [x] Bill of materials export, IFC export and FreeCAD macro export. *(IFC/macro not yet opened in FreeCAD by hand)*
- [ ] Phases 0 to 9 validated on the 3 OSes (Phase 12 comes after the MVP).