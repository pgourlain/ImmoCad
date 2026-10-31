using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ImmoCad.Core.Model;
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
        if (!_vm.IsWalk || !_vm.WalkRoomNames || _vm.Walk is not { } w) return;
        var placed = _vm.WalkLabelCache.Place(w, _camera(), Bounds.Width, Bounds.Height); var taken = new List<Rect>();
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
    }
}
