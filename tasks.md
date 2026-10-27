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
- **Fittings are derived, never drawn:** elbows, tees, reducers and floor crossings are computed from the network graph. The user only manipulates nodes and segments.
- Nullable reference types and implicit usings enabled. User-visible strings go through `Translations` (French and English from the start).
- Releases are Native AOT: no reflection-based `System.Text.Json` (source-generated contexts), no `new Binding(path)` from code. Keep trim/AOT warnings at zero.

## Projects

| Project | Contents |
|---|---|
| `ImmeubleCad.Core` | Building model, history, actions, tools, file format, import pipeline, exports. Non-visual. |
| `ImmeubleCad.Plan` | Copied and adapted wall detection: `LineExtraction` (mask), `MaskTracer` (outlines), plus `WallFitter` (outlines → wall segments). BCL only. |
| `ImmeubleCad.Scene` | Pure C# mesh generation: wall extrusion, slabs, pipe cylinders, fittings, equipment boxes; picking (ray/mesh). BCL only. |
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

- [ ] Create `ImmeubleCad.slnx` with all projects of the table above (empty where needed), targeting `net10.0`.
- [ ] `Directory.Build.props`: nullable, implicit usings, warnings as errors for nullable, `LangVersion latest`.
- [ ] `CoreArchitectureTests`: fail if Core references Avalonia, SkiaSharp, Silk.NET or `System.Drawing`. `PlanArchitectureTests` and `SceneArchitectureTests`: BCL only.
- [ ] DI entry point `ServiceExtensions.AddImmeubleCadServices()` in Core.
- [ ] CI `.github/workflows/ci.yml`: matrix `windows-latest`, `macos-latest`, `ubuntu-latest`, `dotnet build` + `dotnet test`, upload screenshots.
- [ ] `CLAUDE.md` with goal, architecture rules, projects, commands (copy CinnabarSharp's layout).
- [ ] `README.md`, `LICENSE` (MIT), `CHANGELOG.md`.

**Validation**

- [ ] `dotnet test` green on the 3 OSes, locally and in CI.

## Phase 1 — Avalonia shell

- [ ] `ImmeubleCad.Desktop` (Avalonia 12, CommunityToolkit.Mvvm), composition root `AppServices.Build()` shared by `App` and the UI tests.
- [ ] Main window: menu bar, toolbar, left tool strip, center view area with three tabs or a split (Plan / Elevation / 3D), right panels (Levels, Networks, Properties), status bar (tool, cursor position in metres, current level, zoom).
- [ ] Menus defined once as `MenuSpec` records, turned into a `NativeMenu` on macOS and an in-window `Menu` elsewhere (copy CinnabarSharp). Later features bound to a disabled `NotYetImplemented` command.
- [ ] Shortcuts through `PlatformHotkeyConfiguration.CommandModifiers` (⌘ on macOS, Ctrl elsewhere).
- [ ] `IDialogService` (file pickers, save-changes prompt, errors), `FakeDialogService` for tests, `TestHarness` building the real window.
- [ ] Welcome screen: New building, Open, Recent files.

**Validation**

- [ ] App launches on the 3 OSes; HiDPI crisp; macOS app menu present.
- [ ] Headless test `Main_window_shows_three_views` passes on the 3 OSes.

## Phase 2 — Building document, file format, history

- [ ] Model (Core): `Building` → `Level` (name, altitude mm, height mm, background image, calibration) → `Wall`; `Equipment`; `Network` → `Node`, `Segment` (see Phase 6 for networks; create the types now, empty).
- [ ] `Wall`: footprint polygon (`List<PointMm>`, closed) on one level, height defaults to the level height, optional label. Walls from detection and walls drawn by hand share this type.
- [ ] History: `IHistoryItem`, `BuildingHistory` (pointer, clean pointer, `IsDirty` derived), first item `BaseHistoryItem` ("New building" / "Open building").
- [ ] `BuildingActions`: add/delete/rename level, change altitude/height, add/delete/move wall. `HistoryTests.Actions` lists them all and checks exact undo/redo.
- [ ] Events: `IBuildingEventsService` with `BuildingEventItem`, `LevelEventItem`, `NetworkEventItem`; `MainViewModel` subscribes.
- [ ] File format `.immeuble`: a zip with `building.json` (source-generated `System.Text.Json`, schema version field) and the background images of each level under `images/`. Round-trip test.
- [ ] Tabs for several open buildings, `*`/dot for unsaved changes, save prompt on close and quit, File › Open Recent, drag and drop, file on the command line.
- [ ] Undo/Redo menu and toolbar, History panel (click a step to jump to it).

**Validation**

- [ ] Save, close, reopen: model identical (automated round trip).
- [ ] 200 undo/redo cycles on a 10-level building stay responsive.

## Phase 3 — Import plans and detect walls

**3a — Copy detection code from CinnabarSharp**

- [ ] Copy `LineExtraction` (and `SuggestThickness`) from `CinnabarSharp.Core/Photo` and `MaskTracer` from `CinnabarSharp.Vector` into `ImmeubleCad.Plan`. Replace any CinnabarSharp type (`ColorBgra`, `VectorPath`, selection mask…) with small local types: input is a grayscale `byte[]` + width/height, output is polygons in pixels.
- [ ] Copy the matching tests and synthetic plans from `CinnabarSharp.Core.Tests` / `CinnabarSharp.Vector.Tests`; keep pinned checksums.
- [ ] `PROVENANCE.md`: source repository, commit hash, list of copied files and what was changed. Keep license headers (and Pinta's, if any copied file has them) and add a `THIRD-PARTY-NOTICES.txt` entry.
- [ ] Interface in Core: `IWallDetector { WallDetectionResult Detect(GrayImage image, WallDetectionOptions options, CancellationToken ct); }`, implemented by `PlanWallDetector` (adapter over `ImmeubleCad.Plan`). Core decodes the JPG with Magick.NET and passes pixels; `ImmeubleCad.Plan` never sees a file.

**3b — Import wizard (one level at a time)**

- [ ] File › Import Plan…: pick a JPG/PNG/PDF page (PDF optional), choose or create the level (name, altitude, height; defaults: RDC at 0, next level +2 700 mm).
- [ ] Step 1, straighten: Auto deskew (detect dominant orthogonal lines) with manual angle slider.
- [ ] Step 2, calibrate: click two points on the image, type the real distance in metres → pixels per mm stored in `Level.Calibration`. Warn if not calibrated.
- [ ] Step 3, detect: live preview of the wall mask in red over the faded plan; Auto thickness button; sliders for threshold, min thickness, bridge gap, min area.
- [ ] Step 4, review: detected walls shown as polygons over the plan; click to delete, lasso to delete, accept all. Validation adds the walls in **one history step** (`BuildingActions.ImportWalls`).
- [ ] The plan image stays as the level's background (opacity slider, hide/show).

**3c — Align levels**

- [ ] Level › Align with Level Below: click the same 2 reference points (e.g. two corners of the stairwell) on both levels → translation + rotation stored in the level; the background and walls follow. One history step.

**3d — Wall fitting (optional for the MVP, needed for clean editing)**

- [ ] `WallFitter` (Plan): from traced outlines, find pairs of parallel sides → wall center segments with a thickness; keep the polygon when fitting fails.

**Validation**

- [ ] Five real plans (clean CAD export, scanned A4, phone photo at an angle, hand drawing, the author's building): at the suggested settings, every load-bearing wall is detected and text/dimensions are dropped; record failures in `docs/detection-results.md`.
- [ ] A 3-level building imported from 3 JPGs: after alignment, the stairwell walls are superposed within 50 mm.
- [ ] Detection of an A3 plan at 300 dpi takes a few seconds at most, with live preview, on each OS.

## Phase 4 — Plan view and walls by hand

- [ ] `PlanView` control: background image, walls (filled polygons), grid, zoom around the mouse (⌘/Ctrl + wheel, pinch), pan (Space + drag, middle mouse).
- [ ] Level selector (Levels panel and status bar); levels below shown faded ("ghost" of the level below, toggle).
- [ ] Tools in Core implementing `ITool` with `ToolPointer` in **mm** (no Avalonia types), unit-tested: Select (click, box, Shift add), Move, Draw Wall (click-click polyline, thickness option, Enter to finish), Delete.
- [ ] Snapping (`Snapper` in Core): grid, wall corners, wall faces, midpoints, orthogonal 0/90° (45° with a modifier). Snap indicator drawn by the view from a `ToolOverlay`.
- [ ] Measure tool: two clicks → distance shown, nothing added to the model.
- [ ] Properties panel: selected wall's thickness, height, label.

**Validation**

- [ ] Draw a 4-room flat by hand in under 5 minutes, by someone who has never used the app (usability check).
- [ ] Snapping tests pass on the 3 OSes (pure Core).

## Phase 5 — 3D view

- [ ] `ImmeubleCad.Scene`: `MeshBuilder` producing vertex/index buffers (float positions in metres, normals, color per network/category): wall extrusion from footprint polygon and height, floor slab per level (from the outer outline or a bounding polygon), pipe cylinders, fittings (torus segment for elbows, simple tee), equipment boxes. Deterministic; pinned checksum test.
- [ ] `Picking`: ray vs meshes → id of the wall, equipment, node or segment under the mouse. Unit-tested.
- [ ] `Viewport3D` control (Avalonia `OpenGlControlBase` + Silk.NET.OpenGL): orbit (left drag), pan (right or middle drag), zoom (wheel), "fit all", top/front/side/isometric presets. Simple lighting (one directional light + ambient).
- [ ] Display options: show/hide each level, cut above the current level, walls opacity (X-ray mode to see pipes through walls), show/hide each network.
- [ ] Selection is shared between plan, elevation and 3D views (selecting in one highlights in the others).
- [ ] Headless tests: when OpenGL isn't available, render a fallback (wireframe with Avalonia drawing) so screenshots still show something; test the camera math in Scene.

**Validation**

- [ ] A 6-level building with all its walls orbits at 60 fps on an integrated GPU, on each OS.
- [ ] Clicking a wall in 3D selects it in the plan view.

## Phase 6 — Networks model

- [ ] `NetworkType`: HeatingSupply, HeatingReturn, DomesticHotWater, ColdWater, Drainage, Gas (extensible), each with default color, default diameter and material.
- [ ] `Network` = graph: `Node` (id, `PointMm3` position, optional attachment: wall face, equipment port) and `Segment` (node A, node B, outer diameter mm, material). Segments must be axis-aligned (X, Y or Z) in the MVP; 45° later.
- [ ] `FittingResolver` (Core, pure): derives each node's fitting from its segments: end cap, straight joint, elbow 90°, tee, cross, reducer (diameter change), floor/slab crossing (vertical segment crossing a level's altitude), wall crossing. Fully unit-tested.
- [ ] Validation rules (warnings, not errors): unconnected end, segment through a wall without crossing, two networks overlapping, slope missing on drainage.
- [ ] `BuildingActions` for networks: add node/segment, move node, split segment, merge collinear segments, delete, change diameter/type. All in `HistoryTests.Actions`.
- [ ] Networks panel: list of networks, color, visibility, lock.

**Validation**

- [ ] `FittingResolver` tests cover every node configuration; the resolved fittings of a sample building are pinned.

## Phase 7 — Equipment: radiators and risers

- [ ] Equipment library (JSON in resources, editable later): radiator (width, height, depth, supply/return ports), boiler, hot-water tank, manifold, meter, valve. Each has typed **ports** (network type + diameter + position relative to the equipment).
- [ ] Place Equipment tool: drag from the library onto the plan; radiators snap against a wall face at a default height above the floor (150 mm), oriented along the wall.
- [ ] **Riser (colonne)** is not a separate type: it is a set of vertical segments of one network. The Riser tool creates it (see Phase 8a).
- [ ] Connect: drag from a radiator port to a riser or a pipe → creates the connection segments with orthogonal routing (down to a chosen height, along the wall, to the riser). One history step.
- [ ] Properties panel: equipment dimensions, ports, level, label (e.g. "Radiateur séjour").

**Validation**

- [ ] On the author's building: place the radiators of one flat and connect them to the existing heating risers in under 10 minutes.

## Phase 8 — Pipe tools (the core of the UX)

**8a — Risers from the top view (circles)**

- [ ] Riser tool in the plan view: click to place a circle = a vertical pipe at that XY. Options bar: network type, diameter, **from level / to level** (defaults: whole building).
- [ ] Templates: place 2 or 3 risers at once with fixed spacing (e.g. hot + cold water, heating supply + return, or hot + cold + drainage), editable spacing and order. Saved as user templates.
- [ ] Riser snaps to wall corners and faces with an offset (default: 50 mm from each face).

**8b — Pipe on a wall corner from the elevation view**

- [ ] Elevation view: pick a wall (or two walls meeting at a corner) and see it from the front, with level lines and altitudes.
- [ ] In the elevation view, click the corner (or a point on the face) → vertical pipe attached to that corner, offset from both faces; drag to set its top and bottom, snapping to level altitudes.
- [ ] Horizontal pipe along the wall face at a given height above the floor.

**8c — Extent over several levels**

- [ ] Every vertical run has an editable extent "from level X to level Y" (Properties panel) and top/bottom handles in elevation and 3D views that snap to level altitudes; slab crossings are added automatically.

**8d — Direct manipulation with handles**

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

- [ ] Bill of materials: per network, length per diameter and material (rounded up to standard bar lengths, option), count of each fitting type, list of equipment. View in a panel, export to CSV and to a printable PDF.
- [ ] Export 3D to glTF (`.glb`) from `ImmeubleCad.Scene` meshes (viewable in a browser or FreeCAD).
- [ ] Export plans per level and per network to SVG/PDF (with level name, scale, legend).
- [ ] IFC export (optional, later): walls as `IfcWall`, pipes as `IfcPipeSegment`/`IfcPipeFitting` via xBIM.
- [ ] FreeCAD import (optional, later): read a glTF/STEP export of an existing model as a non-editable reference mesh to trace over.

**Validation**

- [ ] The bill of materials of the sample building matches a hand count.
- [ ] The glTF export opens in FreeCAD and in a browser viewer with the right scale.

## Phase 10 — MCP server

- [ ] `ImmeubleCad.Mcp` (official C# SDK), Core only, hosted by the app: `ImmeubleCad --mcp` (headless, stdio) and `--mcp --attach` (relay to the running app over a Unix domain socket, File › Allow AI Agents, off by default). Copy the design from `CinnabarSharp.Mcp` (dispatcher, `McpContext.Run`, allowed folders, `overwrite`/`discardChanges`).
- [ ] Tools: open/new/save/close building, list levels, add level, import plan with detection parameters, list walls, add riser (network, XY, level range), add pipe run (list of points), connect equipment, bill of materials, export glTF/SVG/CSV, undo/redo.
- [ ] Resources: `immeuble://buildings`, `immeuble://buildings/{id}/networks`, `immeuble://equipment-library`.
- [ ] `docs/mcp.md` with `claude mcp add` setup and examples ("add a hot and cold water riser from RDC to R+5 in the corner of the bathroom").
- [ ] `ImmeubleCad.Mcp.Tests`: start the real app with `--mcp` and drive it end to end.

**Validation**

- [ ] Claude Code builds a 2-level building from two JPGs and adds risers through MCP on each OS.

## Phase 11 — Polish and packaging

- [ ] Settings persistence (JSON in the per-OS app-data folder): window, panels, last tool, snapping options, templates.
- [ ] App icon, About dialog, light/dark themes (design tokens like CinnabarSharp's `Themes/`).
- [ ] `packaging/package.sh <rid> <version>`: self-contained, Native AOT, Windows zip, macOS `.app`/`.dmg` (single architecture), Linux tarball with `.desktop` file; `.immeuble` file association.
- [ ] Release workflow: pushing a `v*` tag packages win-x64, linux-x64, osx-arm64, osx-x64 and publishes a GitHub release.
- [ ] Crash log and unsaved work recovery.
- [ ] Onboarding: a sample building opened from the welcome screen, and a 5-step guided tour (import a plan, calibrate, detect, add a riser, extend a pipe).

**Validation**

- [ ] Download the release packages on fresh machines of each OS: the app starts, imports a plan, saves and reopens a building.

## Criteria for the MVP (0.1)

- [ ] Import JPG plans for several levels, calibrate, detect walls, align levels.
- [ ] 3D view of the building with level visibility and X-ray mode.
- [ ] Radiators from the library, risers from the top view (with templates), pipes on wall corners from the elevation view, level extents, extend/elbow/tee with handles.
- [ ] Bill of materials export.
- [ ] Phases 0 to 9 validated on the 3 OSes.