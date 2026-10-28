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
