# ImmoCad
Model a building from floor plans and lay out its pipes. Early development: see [tasks.md](tasks.md).

## Status
Phases 0–12 are implemented and covered by automated tests (see tasks.md); what is left are the checks that need people, other operating systems or other tools (FreeCAD, Blender, real plans, usability tests).
Run: `dotnet run --project src/ImmoCad.Desktop` (add `-- --mcp` for the MCP server). Test: `dotnet test --solution ImmoCad.slnx`.
