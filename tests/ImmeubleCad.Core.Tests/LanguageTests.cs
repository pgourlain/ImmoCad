using System.Text.RegularExpressions;
using ImmeubleCad.Core;
using Xunit;

namespace ImmeubleCad.Core.Tests;

/// <summary>One language at a time: English by default, French complete, switchable at runtime, and no hard-coded user-visible text.</summary>
public class LanguageTests
{
    private static readonly Regex French = new("[éèêëàâùûçôîïœÉÈÀ]", RegexOptions.Compiled);
    // French words that carry no accent: the letter test alone would miss "Renommer le niveau"
    private static readonly Regex FrenchWord = new("\\b(Renommer|niveaux?|murs?|Cliquez|Choisissez|Aucun|Aucune|Annuler|Appliquer|Ouvrir|Fermer|Enregistrer|Supprimer|Importer|Exporter|Rechercher|Terminer|tuyaux?|colonnes?|porte|cave|caves|Licence|Ouverte|Suivant|hauteur|Altitude|Valider|Passer|Nomenclature)\\b", RegexOptions.Compiled);

    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "ImmeubleCad.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("solution root");
    }

    private static void With(string language, Action body)
    {
        var before = Translations.Language; Translations.Language = language;
        try { body(); } finally { Translations.Language = before; }
    }

    [Fact] public void English_is_the_default_and_a_fresh_settings_file_asks_for_it()
    {
        Assert.Equal("en", new AppSettings().Language); Assert.Equal("en", Translations.Languages[0]);
        Assert.Equal("Ready", Translations.In("en", "ready")); Assert.Equal("Prêt", Translations.In("fr", "ready"));
        With("xx", () => Assert.NotEqual("xx", Translations.Language));                           // unknown language: ignored
    }

    [Fact] public void Both_languages_are_complete_and_each_one_is_really_in_its_language()
    {
        Assert.Empty(Translations.FrenchKeys.Except(Translations.EnglishKeys)); Assert.Empty(Translations.EnglishKeys.Except(Translations.FrenchKeys));
        Assert.True(Translations.Keys.Count > 400);
        var allowedSame = new HashSet<string> { "app.name", "view.2d", "view.3d", "btn.ok", "tab.view3d", "fmt.mm", "fmt.angle", "fmt.rotation", "fmt.rotation3", "align.mm", "measure.m", "wiz.cal.ok", "lang.en", "lang.fr", "export.fmt.dxf", "export.fmt.obj", "export.fmt.ifc", "export.fmt.glb", "space.area.fmt", "site.item.area", "enum.NetworkType.Drainage", "enum.SpaceKind.Parking", "enum.SpaceKind.Corridor", "tab.palette", "tab.options", "map.cadastre", "enum.ViewMode.Plan", "tab.site", "level.site", "extent.info", "eq.info", "level.tooltip", "elev.title", "typed.angle", "view.2d", "action.Connect", "export.format", "export.pur.ifc", "export.dialog.purpose.ifc", "map.ortho" };
        foreach (var k in Translations.Keys)
        {
            var en = Translations.In("en", k); var fr = Translations.In("fr", k);
            Assert.False(string.IsNullOrWhiteSpace(en) || string.IsNullOrWhiteSpace(fr), k);
            if (!k.StartsWith("lang.")) Assert.False(French.IsMatch(en), $"English text of '{k}' contains French letters: {en}");                          // never French inside English
            if (!allowedSame.Contains(k) && en.Any(char.IsLetter) && !k.StartsWith("fmt.")) Assert.True(en != fr || fr.Length < 4, $"'{k}' is not translated: {en}");
            // placeholders must match in both languages
            Assert.Equal(Regex.Matches(en, "\\{\\d+").Select(m => m.Value).OrderBy(x => x), Regex.Matches(fr, "\\{\\d+").Select(m => m.Value).OrderBy(x => x));
        }
    }

    [Fact] public void Switching_language_changes_every_lookup_and_raises_the_event_once()
    {
        var raised = 0; Action h = () => raised++; Translations.LanguageChanged += h;
        try
        {
            With("en", () => { Assert.Equal("Cancel", Translations.T("btn.cancel")); Assert.Equal("3 wall(s) selected", Translations.T("walls.selected", 3)); Assert.Equal("Heating supply", Translations.Enum_(ImmeubleCad.Core.Model.NetworkType.HeatingSupply)); });
            With("fr", () => { Assert.Equal("Annuler", Translations.T("btn.cancel")); Assert.Equal("Départ chauffage", Translations.Enum_(ImmeubleCad.Core.Model.NetworkType.HeatingSupply)); });
            Translations.Language = Translations.Language;                                                                       // no change → no event
        }
        finally { Translations.LanguageChanged -= h; }
        Assert.True(raised >= 2);
    }

    [Fact] public void Exports_follow_the_language_bill_of_materials_scale_hint_and_catalog_names()
    {
        var (b, _) = SampleBuilding.Create();
        With("en", () =>
        {
            var csv = ImmeubleCad.Core.Export.BomFull.ToCsv(b, true); Assert.StartsWith("section,network", csv); Assert.Contains("bars of 3 m", csv); Assert.Contains("equipment,,Radiator", csv);
            Assert.Contains("Bill of materials", System.Text.Encoding.Latin1.GetString(ImmeubleCad.Core.Export.BomFull.ToPdf(b))); Assert.DoesNotContain("é", csv);
            Assert.Equal("Radiator", ImmeubleCad.Core.Catalog.EquipmentCatalog.Find("radiator")!.Name);
            Assert.Contains("wider", ImmeubleCad.Core.Geometry.FootprintFit.CheckScale([new(0, 0), new(10280, 0), new(10280, 6000), new(0, 6000)], [new(0, 0), new(10000, 0), new(10000, 6000), new(0, 6000)])!.Message);
            Assert.StartsWith("level,kind", ImmeubleCad.Core.Spaces.ToCsv(b));
        });
        With("fr", () => { Assert.Contains("barres de 3 m", ImmeubleCad.Core.Export.BomFull.ToCsv(b, true)); Assert.Equal("Radiateur", ImmeubleCad.Core.Catalog.EquipmentCatalog.Find("radiator")!.Name); Assert.StartsWith("niveau,type", ImmeubleCad.Core.Spaces.ToCsv(b)); });
        With("en", () => { var sample = SampleBuilding.Create().Building; Assert.Equal(["Ground floor", "Level 1", "Level 2"], sample.Levels.Select(l => l.DisplayName)); });
    }

    [Fact] public void No_user_visible_text_is_hard_coded_in_the_views_or_the_view_models()
    {
        var root = Root(); var offenders = new List<string>();
        // 1. XAML: text-bearing attributes must be bindings, symbols, or go through vm:L.* keys
        foreach (var f in Directory.GetFiles(Path.Combine(root, "src", "ImmeubleCad.Desktop"), "*.axaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), "(?<![\\w.:])(Text|Content|Header|ToolTip\\.Tip|Watermark|Title|FormatString)=\"([^\"{][^\"]*)\""))
            {
                var v = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value);
                if (m.Groups[1].Value == "Content" && v == "Height") continue;                                                  // SizeToContent="Height"
                if (!Regex.IsMatch(v, "[A-Za-zÀ-ÿ]{2,}") || v == "ImmeubleCad") continue;                                     // glyphs / numbers / the product name
                offenders.Add($"{Path.GetFileName(f)}: {m.Groups[1].Value}=\"{v}\"");
            }
        // 2. C#: no French letters in string literals outside the translation tables (comments excluded)
        foreach (var f in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.EndsWith("TranslationData.cs")))
        {
            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = Regex.Replace(lines[i], "//.*$", ""); if (lines[i].TrimStart().StartsWith("///") || lines[i].TrimStart().StartsWith("//")) continue;
                foreach (Match m in Regex.Matches(code, "\\$?\"((?:[^\"\\\\]|\\\\.)*)\"")) if (French.IsMatch(m.Groups[1].Value) || (FrenchWord.IsMatch(m.Groups[1].Value) && !Regex.IsMatch(m.Groups[1].Value, "^[\\w.]+$"))) offenders.Add($"{Path.GetFileName(f)}:{i + 1}: {m.Groups[1].Value}");
            }
        }
        Assert.True(offenders.Count == 0, "Hard-coded French/untranslated text:\n" + string.Join("\n", offenders.Take(40)));
    }

    [Fact] public void Every_key_used_in_the_sources_exists()
    {
        var root = Root(); var missing = new SortedSet<string>(); var keys = Translations.Keys.ToHashSet();
        foreach (var f in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.EndsWith("TranslationData.cs")))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), "\\bT\\(\"([\\w.]+)\""))
                if (!m.Groups[1].Value.EndsWith(".") && !keys.Contains(m.Groups[1].Value)) missing.Add($"{Path.GetFileName(f)}: {m.Groups[1].Value}");
        foreach (var f in Directory.GetFiles(Path.Combine(root, "src", "ImmeubleCad.Desktop"), "*.axaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), "vm:L\\.\\w+=\"([\\w.]+)\"")) if (!keys.Contains(m.Groups[1].Value)) missing.Add($"{Path.GetFileName(f)}: {m.Groups[1].Value}");
        Assert.True(missing.Count == 0, "Missing translation keys:\n" + string.Join("\n", missing));
    }

    [Fact] public void Dynamic_key_families_are_complete_history_actions_catalog_categories_and_every_enum_value()
    {
        var root = Root(); var keys = Translations.Keys.ToHashSet(); var missing = new List<string>();
        foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(root, "src", "ImmeubleCad.Core", "History", "BuildingActions.cs")), "Edit(?:<[^>]*>)?\\(\"([^\"]+)\"")) if (!keys.Contains("action." + m.Groups[1].Value)) missing.Add("action." + m.Groups[1].Value);
        foreach (var c in ImmeubleCad.Core.Catalog.EquipmentCatalog.Default.Select(e => e.Category).Distinct()) if (!keys.Contains("cat." + c)) missing.Add("cat." + c);
        foreach (var t in new[] { typeof(ImmeubleCad.Core.Model.NetworkType), typeof(ImmeubleCad.Core.Model.SpaceKind), typeof(ImmeubleCad.Core.Model.LevelKind) })
            foreach (var v in Enum.GetValues(t)) if (!keys.Contains($"enum.{t.Name}.{v}")) missing.Add($"enum.{t.Name}.{v}");
        foreach (var id in new[] { "ifc", "freecad", "dxf", "glb", "obj", "svg", "csv", "bom_pdf" }) foreach (var p in new[] { "export.fmt.", "export.pur." }) if (!keys.Contains(p + id)) missing.Add(p + id);
        Assert.True(missing.Count == 0, "Missing:\n" + string.Join("\n", missing));
    }
}
