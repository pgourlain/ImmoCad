namespace ImmeubleCad.Core;

/// <summary>User-visible strings, French and English. Missing keys fall back to French, then to the key itself.</summary>
public static class Translations
{
    public static string Language { get; set; } = "fr";

    public static string T(string key, params object[] args)
    {
        var table = Language == "en" ? En : Fr;
        var text = table.TryGetValue(key, out var v) ? v : Fr.TryGetValue(key, out var f) ? f : key;
        return args.Length == 0 ? text : string.Format(text, args);
    }

    /// <summary>Every key must exist in both languages (checked by a test).</summary>
    public static IReadOnlyCollection<string> FrenchKeys => Fr.Keys;
    public static IReadOnlyCollection<string> EnglishKeys => En.Keys;

    private static readonly Dictionary<string, string> Fr = new()
    {
        ["ready"] = "Prêt", ["no.selection"] = "Aucune sélection", ["walls.selected"] = "{0} mur(s) sélectionné(s)", ["pipe.selected"] = "1 tuyau sélectionné",
        ["hint.select"] = "Cliquez un mur pour le sélectionner, glissez pour déplacer.", ["hint.draw.wall"] = "Cliquez les points du mur — Entrée pour finir — Échap pour annuler",
        ["hint.draw.pipe"] = "Cliquez les points du tuyau — Entrée pour finir — Échap pour annuler", ["hint.riser"] = "Cliquez pour poser une colonne (collée à 50 mm des murs)",
        ["hint.place"] = "Cliquez pour poser l'objet de la palette", ["hint.align"] = "Cliquez un point de ce niveau, puis le même point sur le niveau du dessous (au moins 2 paires)",
        ["hint.door"] = "Cliquez sur un mur pour poser une porte (clic sur une porte : inverser le sens)", ["hint.window"] = "Cliquez sur un mur pour poser une fenêtre",
        ["hint.space"] = "Cliquez dans une pièce fermée par des murs", ["hint.measure"] = "Cliquez deux points pour mesurer une distance", ["hint.number"] = "Cliquez les caves dans l'ordre pour les numéroter — Entrée pour valider",
        ["status.walls.moved"] = "{0} mur(s) déplacé(s) vers {1}", ["status.need.selection"] = "Sélectionnez d'abord des murs", ["status.no.level.there"] = "Pas d'étage dans cette direction",
        ["menu.file"] = "_Fichier", ["menu.edit"] = "_Édition", ["menu.level"] = "_Niveau", ["menu.view"] = "_Affichage", ["menu.help"] = "_Aide",
        ["dialog.save.changes"] = "Enregistrer les modifications de « {0} » ?", ["dialog.save"] = "Enregistrer", ["dialog.discard"] = "Ne pas enregistrer", ["dialog.cancel"] = "Annuler",
        ["measure.result"] = "Distance : {0:0.00} m", ["spaces.numbered"] = "{0} espace(s) numéroté(s)", ["door.added"] = "Porte posée ({0} mm)", ["window.added"] = "Fenêtre posée",
        ["space.added"] = "Pièce créée : {0:0.0} m²", ["space.open"] = "Pièce non fermée : les murs ne l'entourent pas complètement",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["ready"] = "Ready", ["no.selection"] = "Nothing selected", ["walls.selected"] = "{0} wall(s) selected", ["pipe.selected"] = "1 pipe selected",
        ["hint.select"] = "Click a wall to select it, drag to move it.", ["hint.draw.wall"] = "Click the wall points — Enter to finish — Esc to cancel",
        ["hint.draw.pipe"] = "Click the pipe points — Enter to finish — Esc to cancel", ["hint.riser"] = "Click to place a riser (snapped 50 mm from the walls)",
        ["hint.place"] = "Click to place the palette object", ["hint.align"] = "Click a point on this level, then the same point on the level below (at least 2 pairs)",
        ["hint.door"] = "Click a wall to place a door (click a door: flip the swing)", ["hint.window"] = "Click a wall to place a window",
        ["hint.space"] = "Click inside a room closed by walls", ["hint.measure"] = "Click two points to measure a distance", ["hint.number"] = "Click the cellars in order to number them — Enter to confirm",
        ["status.walls.moved"] = "{0} wall(s) moved to {1}", ["status.need.selection"] = "Select some walls first", ["status.no.level.there"] = "No level in that direction",
        ["menu.file"] = "_File", ["menu.edit"] = "_Edit", ["menu.level"] = "_Level", ["menu.view"] = "_View", ["menu.help"] = "_Help",
        ["dialog.save.changes"] = "Save the changes to “{0}”?", ["dialog.save"] = "Save", ["dialog.discard"] = "Don't save", ["dialog.cancel"] = "Cancel",
        ["measure.result"] = "Distance: {0:0.00} m", ["spaces.numbered"] = "{0} space(s) numbered", ["door.added"] = "Door placed ({0} mm)", ["window.added"] = "Window placed",
        ["space.added"] = "Room created: {0:0.0} m²", ["space.open"] = "Room is not closed: the walls do not fully surround it",
    };
}
