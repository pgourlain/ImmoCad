using System.Text.Json;
using ImmoCad.Core.Model;

namespace ImmoCad.Core.History;

public interface IHistoryItem
{
    string Name { get; }
    void Undo(Building b);
    void Redo(Building b);
}

/// <summary>Snapshot-based item: exact undo/redo by restoring serialized state.</summary>
public sealed class SnapshotItem(string name, string before, string after) : IHistoryItem
{
    public string Name { get; } = name;
    public void Undo(Building b) => BuildingJson.Restore(b, before);
    public void Redo(Building b) => BuildingJson.Restore(b, after);
}

public sealed class BuildingHistory
{
    private readonly List<IHistoryItem> _items = new();
    private int _pointer = -1;
    private int _clean = -1;
    public IReadOnlyList<IHistoryItem> Items => _items;
    public int Pointer => _pointer;
    public bool IsDirty => _pointer != _clean;
    public bool CanUndo => _pointer >= 0;
    public bool CanRedo => _pointer < _items.Count - 1;
    public event Action? Changed;

    public void Push(IHistoryItem item)
    {
        _items.RemoveRange(_pointer + 1, _items.Count - _pointer - 1);
        if (_clean > _pointer) _clean = -2;
        _items.Add(item); _pointer++;
        Changed?.Invoke();
    }
    public void MarkClean() { _clean = _pointer; Changed?.Invoke(); }
    public void Undo(Building b) { if (!CanUndo) return; _items[_pointer].Undo(b); _pointer--; Changed?.Invoke(); }
    public void Redo(Building b) { if (!CanRedo) return; _pointer++; _items[_pointer].Redo(b); Changed?.Invoke(); }
    public void JumpTo(Building b, int index)
    {
        while (_pointer > index) Undo(b);
        while (_pointer < index) Redo(b);
    }
}

public static class BuildingJson
{
    public static string Serialize(Building b) => JsonSerializer.Serialize(b, BuildingJsonContext.Default.Building);
    public static Building Deserialize(string json) =>
        JsonSerializer.Deserialize(json, BuildingJsonContext.Default.Building) ?? throw new InvalidDataException("empty building");
    public static void Restore(Building target, string json)
    {
        var s = Deserialize(json);
        target.SchemaVersion = s.SchemaVersion; target.Name = s.Name;
        target.Levels = s.Levels; target.Networks = s.Networks; target.Equipment = s.Equipment; target.Site = s.Site; target.WorldFrame = s.WorldFrame;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Building))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = false, IncludeFields = false)]
public partial class BuildingJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
