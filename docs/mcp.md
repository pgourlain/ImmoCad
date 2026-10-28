# ImmoCad MCP server

The desktop executable hosts an MCP server over stdio: `ImmoCad --mcp` (headless, no window). Logs go to the log file and stderr only; stdout is the protocol.

## Setup with Claude Code

```bash
claude mcp add immocad -- /path/to/ImmoCad --mcp
# from source
claude mcp add immocad -- dotnet run --project src/ImmoCad.Desktop -- --mcp
```

## Tools

| Tool | Purpose |
|---|---|
| `new`, `open`, `save`, `close` | Building lifecycle. `open`/`new`/`close` refuse when there are unsaved changes unless `discardChanges=true`; `save`/`export` refuse to overwrite when `overwrite=false`. |
| `add_level`, `list_levels` | Levels (altitude and height in mm). |
| `import_plan` | Calibrate (`pxPerMm`) and detect walls from a JPG/PNG. One history step. |
| `add_wall`, `list_walls` | Rectangular walls and listing. |
| `add_riser`, `add_riser_template`, `add_pipe_run`, `connect_equipment` | Networks. |
| `bill_of_materials`, `export` | `export` formats: `ifc`, `freecad`, `dxf`, `obj`, `glb`, `bom_csv`, `bom_pdf`. |
| `undo`, `redo` | History. |

Files are only read/written inside the allowed folders (temp folder and the user's home by default).

## Resources

`immocad://buildings`, `immocad://buildings/{id}/networks`, `immocad://equipment-library`.

## Example prompts

- "Create a building with levels RDC at 0 and R+1 at 2700, import `~/plans/rdc.jpg` at 0.1 px/mm on RDC."
- "Add a hot and cold water riser from RDC to R+5 in the corner of the bathroom" → `add_riser_template` with `types="ColdWater,DomesticHotWater"`, `fromZMm=0`, `toZMm=16200`.
- "Export the IFC and the bill of materials as PDF to `~/out/`."
