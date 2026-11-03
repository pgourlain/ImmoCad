using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ImmoCad.Core.Model;
using ImmoCad.Core.Networks;
using ImmoCad.Core.Walk;
using ImmoCad.Scene;

namespace ImmoCad.Desktop;

/// <summary>
/// Room names floating in the first-person view, over the OpenGL surface. The placement (what is visible, in front, near enough, not behind a wall) is <see cref="WalkLabels"/>;
/// this only draws: a dark rounded plate with a dot of the room kind's colour and the text, fading with distance, never two plates on top of each other.
/// </summary>
public sealed class RoomLabelsView : Control
{
    private readonly MainViewModel _vm; private readonly Func<WalkCamera> _camera;

    public RoomLabelsView(MainViewModel vm, Func<WalkCamera> camera) { _vm = vm; _camera = camera; IsHitTestVisible = false; ZIndex = 10; IsVisible = false; }

    /// <summary>One colour per kind of room, so the boiler room or the bike room is recognised before it is read.</summary>
    public static Color KindColor(SpaceKind k) => k switch
    {
        SpaceKind.Cellar => Color.FromRgb(176, 124, 72), SpaceKind.BikeRoom => Color.FromRgb(86, 190, 110), SpaceKind.BoilerRoom => Color.FromRgb(226, 84, 70),
        SpaceKind.Corridor => Color.FromRgb(120, 160, 210), SpaceKind.Parking => Color.FromRgb(160, 164, 172), SpaceKind.ServiceShaft => Color.FromRgb(238, 160, 60),
        SpaceKind.Stairwell => Color.FromRgb(170, 120, 210), SpaceKind.Apartment => Color.FromRgb(90, 150, 230), _ => Color.FromRgb(200, 200, 200),
    };

    public override void Render(DrawingContext ctx)
    {
        if (!_vm.IsWalk || _vm.Walk is not { } w) return;
        var taken = new List<Rect>();
        foreach (var n in _vm.WalkLabelCache.PlaceNetwork(w, _camera(), Bounds.Width, Bounds.Height, _vm.FocusedNetworkId))
        {   // the name of the network looked at (or picked in the tab) over each of its pipes and risers: first, so nothing covers it
            var a = (byte)(255 * n.Alpha); var size = Math.Clamp(14 - n.Distance / 1000.0 * 0.25, 11, 14); var c = NetworkQueries.ColorOf(n.Type);
            var text = new FormattedText(n.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.Bold), size, new SolidColorBrush(Color.FromArgb(a, 255, 255, 255)));
            var plate = new Rect(n.X - text.Width / 2 - 20, n.Y - text.Height / 2 - 5, text.Width + 32, text.Height + 10);
            if (taken.Any(r => r.Intersects(plate))) continue; taken.Add(plate);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(215 * n.Alpha), 22, 24, 30)), new Pen(new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)), 2), plate, 8, 8);
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)), null, new Point(plate.X + 13, plate.Center.Y), 4.5, 4.5);
            ctx.DrawText(text, new Point(plate.X + 24, plate.Y + 5));
        }
        if (!_vm.WalkRoomNames) return;
        var placed = _vm.WalkLabelCache.Place(w, _camera(), Bounds.Width, Bounds.Height);
        foreach (var l in placed)
        {
            var size = Math.Clamp(15 - l.Distance / 1000.0 * 0.3, 11, 15); var a = (byte)(255 * l.Alpha);
            var text = new FormattedText(l.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.SemiBold), size, new SolidColorBrush(Color.FromArgb(a, 255, 255, 255)));
            var plate = new Rect(l.X - text.Width / 2 - 20, l.Y - text.Height / 2 - 5, text.Width + 32, text.Height + 10);
            if (taken.Any(r => r.Intersects(plate))) continue; taken.Add(plate);                                                                  // nearest first: a farther name never covers a nearer one
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(200 * l.Alpha), 22, 24, 30)), null, plate, 8, 8);
            var c = KindColor(l.Kind); ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)), null, new Point(plate.X + 13, plate.Center.Y), 4.5, 4.5);
            ctx.DrawText(text, new Point(plate.X + 24, plate.Y + 5));
        }
        foreach (var d in _vm.WalkLabelCache.PlaceDoors(w, _camera(), Bounds.Width, Bounds.Height))
        {   // the number of a door: a small light plate just above the opening, under the room names
            var a = (byte)(255 * d.Alpha); var size = Math.Clamp(13 - d.Distance / 1000.0 * 0.2, 10, 13);
            var text = new FormattedText(d.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.Bold), size, new SolidColorBrush(Color.FromArgb(a, 30, 34, 44)));
            var plate = new Rect(d.X - text.Width / 2 - 7, d.Y - text.Height / 2 - 3, text.Width + 14, text.Height + 6);
            if (taken.Any(r => r.Intersects(plate))) continue; taken.Add(plate);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(235 * d.Alpha), 250, 250, 245)), new Pen(new SolidColorBrush(Color.FromArgb(a, 90, 96, 110)), 1), plate, 5, 5);
            ctx.DrawText(text, new Point(plate.X + 7, plate.Y + 3));
        }
    }
}
