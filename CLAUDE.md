# ImmoCad
Desktop app (Avalonia 12, .NET 10) to model a building from plans and lay out heating/water/drainage networks. See tasks.md for the roadmap.

## Rules
- Lengths are `int` millimetres. X east, Y north, Z up.
- `ImmoCad.Core` is non-visual (no Avalonia/Skia/Silk.NET/System.Drawing). `Plan` and `Scene` are BCL only.
- `BuildingActions` is the only way to edit a building; one action = one history item.
- Logging via `ILogger<T>`; no reflection JSON (source-generated contexts).

## Commands
- `dotnet build ImmoCad.slnx`
- `dotnet test --solution ImmoCad.slnx` (Microsoft.Testing.Platform runner, see global.json)
- `dotnet run --project src/ImmoCad.Desktop [-- --mcp]`

## Language (one at a time)
- Every user-visible string goes through `Translations.T("key")` (English default, French complete, `Core/TranslationData.cs`). In XAML use `vm:L.Text|Content|Header|Tip|Watermark|Title|Format="key"` (attached properties, no reflection bindings). Never hard-code UI text.
- Generated names (levels, rooms, cellars, sample) are tokens (`Translations.Token("level.n", 2)`); show `DisplayName`, never `Name`.
- The language changes at runtime (View › Language); `LanguageTests` fail on a missing key, French letters in English text, or a hard-coded literal.

## Testing notes
- Headless UI tests: async bodies must use `Session.RunAsync(async () => …)`; plain `Dispatch(async () => …)` binds to the `Action` overload and silently skips the assertions.
- Tests run one at a time (language is global state) and start in French; set `Translations.Language` explicitly in tests about English.
- Never read Avalonia properties from a pool thread (capture them first).
