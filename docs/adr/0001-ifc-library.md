# ADR 0001 — IFC export without an IFC library

**Status:** accepted (for 0.1), to revisit.

**Context.** The roadmap asks to evaluate xBIM Essentials and GeometryGym IFC (license, Native AOT). xBIM Essentials uses reflection heavily and is not Native-AOT friendly; GeometryGym IFC (OpenSource edition) is LGPL-licensed, which complicates a statically linked AOT binary.

**Decision.** Write IFC4 STEP text directly (`IfcExporter`): project/site/building/storeys, walls, pipe segments, distribution systems. No dependency, AOT-safe, deterministic output.

**Consequences.** Geometry representations (extruded solids), property sets and colours must be written by hand; validate each addition against FreeCAD and an online IFC viewer (see tasks.md validation). If requirements outgrow this, re-evaluate xBIM after its AOT story improves.
