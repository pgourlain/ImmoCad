using System.Diagnostics;
using Avalonia.Input;
using Avalonia.Threading;
using ImmoCad.Core.Walk;

namespace ImmoCad.Desktop;

/// <summary>
/// Real-time side of the walk: pressed keys → <see cref="WalkInput"/> every frame, applied to the view model's <see cref="WalkState"/>. Frames are requested only while something moves.
/// Letters are read as physical keys, so Z Q S D (AZERTY) and W A S D (QWERTY) are in the same place; the arrows work everywhere.
/// </summary>
public sealed class WalkDriver
{
    private readonly MainViewModel _vm; private readonly Func<ViewportController> _controller; private readonly Action _changed;
    private readonly HashSet<PhysicalKey> _letters = new(); private readonly HashSet<Key> _arrows = new(); private bool _shift;
    private DispatcherTimer? _timer; private long _last;

    public WalkDriver(MainViewModel vm, Func<ViewportController> controller, Action changed) { _vm = vm; _controller = controller; _changed = changed; }

    public bool Running => _timer != null;
    public void Start()
    {
        if (_timer != null) return;
        _last = Stopwatch.GetTimestamp(); _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
        _timer.Start();
    }
    public void Stop() { _timer?.Stop(); _timer = null; Clear(); }
    /// <summary>All keys released: used when the window loses focus, otherwise the walk would go on by itself.</summary>
    public void Clear() { _letters.Clear(); _arrows.Clear(); _shift = false; }

    private static readonly HashSet<PhysicalKey> Letters = [PhysicalKey.W, PhysicalKey.A, PhysicalKey.S, PhysicalKey.D, PhysicalKey.Q, PhysicalKey.E];

    /// <summary>Returns true when the key is for the walk (the window then ignores it: no tool shortcut fires while walking).</summary>
    public bool OnKeyDown(KeyEventArgs e)
    {
        if (!_vm.IsWalk) return false;
        _shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.KeyModifiers.HasFlag(MenuFactory.CommandModifiers)) return false;                    // ⌘/Ctrl combinations (save…) are not ours
        switch (e.Key)
        {
            case Key.Escape: _vm.LeaveWalkCommand.Execute(null); return true;
            case Key.Up or Key.Down or Key.Left or Key.Right: _arrows.Add(e.Key); return true;
            case Key.Home: _vm.Walk?.ResetEye(); Touch(); return true;
            case Key.PageUp: _vm.LevelUpCommand.Execute(null); Touch(); return true;
            case Key.PageDown: _vm.LevelDownCommand.Execute(null); Touch(); return true;
            case Key.LeftShift or Key.RightShift: _shift = true; return true;
        }
        if (e.PhysicalKey == PhysicalKey.X) { _vm.ToggleWalkGhostCommand.Execute(null); Touch(); return true; }
        if (Letters.Contains(e.PhysicalKey)) { _letters.Add(e.PhysicalKey); return true; }
        return true;                                                                                  // anything else is swallowed too: W, V, Delete… must not start a tool in the dark
    }

    public void OnKeyUp(KeyEventArgs e)
    {
        _shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift); _arrows.Remove(e.Key); _letters.Remove(e.PhysicalKey);
    }

    /// <summary>Applies the state to the camera and asks for a frame (after a jump, a level change…).</summary>
    public void Touch() { if (_vm.Walk is { } w) { _controller().ApplyWalk(w); _controller().Invalidate?.Invoke(); } _changed(); }

    private void Tick()
    {
        var now = Stopwatch.GetTimestamp(); var dt = Math.Clamp((now - _last) / (double)Stopwatch.Frequency, 0, 0.1); _last = now;
        if (_vm.Walk is not { } w) return;
        double fwd = (_letters.Contains(PhysicalKey.W) ? 1 : 0) - (_letters.Contains(PhysicalKey.S) ? 1 : 0) + (_arrows.Contains(Key.Up) ? 1 : 0) - (_arrows.Contains(Key.Down) ? 1 : 0);
        double str = (_letters.Contains(PhysicalKey.D) ? 1 : 0) - (_letters.Contains(PhysicalKey.A) ? 1 : 0);
        double vert = (_letters.Contains(PhysicalKey.E) ? 1 : 0) - (_letters.Contains(PhysicalKey.Q) ? 1 : 0);
        double turn = (_arrows.Contains(Key.Left) ? 1 : 0) - (_arrows.Contains(Key.Right) ? 1 : 0);
        var before = (w.X, w.Y, w.EyeZ, w.Yaw);
        if (turn != 0) w.Turn(turn * 1.6 * dt, 0);
        w.Update(dt, new WalkInput(Math.Clamp(fwd, -1, 1), Math.Clamp(str, -1, 1), Math.Clamp(vert, -1, 1), _shift));
        if (before != (w.X, w.Y, w.EyeZ, w.Yaw)) Touch();
    }
}
