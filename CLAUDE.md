# ImmeubleCad
Desktop app (Avalonia 12, .NET 10) to model a building from plans and lay out heating/water/drainage networks. See tasks.md for the roadmap.

## Rules
- Lengths are `int` millimetres. X east, Y north, Z up.
- `ImmeubleCad.Core` is non-visual (no Avalonia/Skia/Silk.NET/System.Drawing). `Plan` and `Scene` are BCL only.
- `BuildingActions` is the only way to edit a building; one action = one history item.
- Logging via `ILogger<T>`; no reflection JSON (source-generated contexts).

## Commands
- `dotnet build ImmeubleCad.slnx`
- `dotnet test --solution ImmeubleCad.slnx` (Microsoft.Testing.Platform runner, see global.json)
- `dotnet run --project src/ImmeubleCad.Desktop [-- --mcp]`
