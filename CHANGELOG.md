# Changelog
## Unreleased
- Core model, history, actions, `.immeuble` file format; Plan detection/straightening; Scene meshes; Avalonia shell with plan view; MCP server (levels, riser, BOM, undo/redo).
- Keyboard shortcuts (undo/redo, select all, copy/cut/paste/duplicate, save/open/new/close/export, zoom, tools V/W/P/D/N/S/M, Delete, Escape, F9).
- Plan: adaptive grid with scale bar, snap to grid (F9), origin marker, Space+drag / trackpad pan, Ctrl(⌘)+drag rotation, riser and door/window selection with properties, wall alignment (centre/left/right, Space) with dashed preview.
- 3D: orientation view cube. Properties panel scrolls.
- Import wizard: zoom/pan (wheel, Space+drag, trackpad), alignment guides, pick two points along a wall to make it horizontal/vertical, live rotation preview.
- Import wizard: ±0.1° rotation buttons, scale and position taken from an already imported level.
- Plan: dashed previews before placing doors, windows, risers, palette objects and rooms; rooms can be selected, renamed and deleted; Delete now works on pipes, risers and equipment.
- Riser templates "cold + hot + hot return" and "drainage only" (new Hot water return network type).
- Front / right / back / left views of the whole building (never from below), with pipe drawing at a chosen depth.
- Plan: snap keeps the grid along straight lines and its reach follows the zoom; a green ring + cross with the tool name shows where the next click lands; dashed ghosts for every palette object (valves on pipes included).
- Walk mode (G, or the Walk button): first-person view at eye height through the corridors of a level, to see the pipes and objects where they are. Drag to look, click the floor to glide there, arrows or Z Q S D (physical keys) to move, E and A for the eye height, Page Up/Down or the level bar to change level. Walls, floors and ceilings are drawn in perspective (OpenGL only); doors and windows cut the 3D walls. Ghost mode (X) lets you cross walls, dissolves the walls around you and shows the pipes inside them dimmed. A map of the level, the room you are in, hover labels and a legend of the networks are drawn over the view. The world limit is automatic, or set from the visible plan (View menu). Read-only: nothing is recorded in the history.
- Room tool: the Options tab has "Openings closed — up to … mm" (default 2000, 0 = only fully closed rooms, kept in the settings). Openings up to that width are closed whatever the position of the cursor in the room (it used to depend on how far the cursor was from a wall), so a room with wide doors or bays gets its own preview instead of merging with the hall behind them.
- Walk mode: room names float in the view (key R or the Names button, on by default): the name you typed, or the kind ("Bike room") when the name was generated, with the room number and a colour per kind; only rooms in front of you, near enough and not behind a wall (ghost mode sees through). The map shows the names and lights the room you are in.
