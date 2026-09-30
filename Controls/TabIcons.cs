using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Apathy.Controls;

/// <summary>
/// Vector tab icons, all laid out on a 32x32 grid (straight strokes sit on whole pixels where
/// possible so they stay crisp at 100%). Use with <see cref="GeometryIcon"/>:
/// <c>Data="{x:Static c:TabIcons.Crosshair}"</c>.
/// </summary>
public static class TabIcons
{
    public const double GridSize = 32;

    public static Geometry Crosshair { get; } = BuildCrosshair();

    public static Geometry Sun { get; } = BuildSun();

    public static Geometry Lua { get; } = BuildLua();

    public static Geometry Enemy { get; } = BuildEnemy();

    public static Geometry Teammate { get; } = BuildTeammate();

    public static Geometry Globe { get; } = BuildGlobe();

    public static Geometry Eye { get; } = BuildEye();

    public static Geometry Grenade { get; } = BuildGrenade();

    // Weapon silhouettes: same unit scale as the other icons, but ~60 wide x 16 tall.
    public static Geometry Pistol { get; } = BuildPistol();

    public static Geometry Smg { get; } = BuildSmg();

    public static Geometry Rifle { get; } = BuildRifle();

    public static Geometry Shotgun { get; } = BuildShotgun();

    public static Geometry MachineGun { get; } = BuildMachineGun();

    public static Geometry Sniper { get; } = BuildSniper();

    // Scope ring with four ticks crossing it and a center dot. The ring fills most of the grid so the
    // icon has about the same visual weight as the glyph icons (gear, floppy).
    private static Geometry BuildCrosshair()
    {
        Geometry icon = Geometry.Combine(Circle(16, 16, 12.5), Circle(16, 16, 10.1), GeometryCombineMode.Exclude, null);
        Rect[] ticks =
        [
            new(15, 0.5, 2, 9.5),
            new(15, 22, 2, 9.5),
            new(0.5, 15, 9.5, 2),
            new(22, 15, 9.5, 2),
        ];
        foreach (var tick in ticks)
            icon = Union(icon, new RectangleGeometry(tick));

        return Frozen(Scaled(Union(icon, Circle(16, 16, 2)), 1.16));
    }

    // Sun: ring with the left half filled, surrounded by eight rounded rays.
    private static Geometry BuildSun()
    {
        var innerRightHalf = Geometry.Combine(Circle(16, 16, 7.3), new RectangleGeometry(new Rect(16, 0, 16, 32)),
            GeometryCombineMode.Intersect, null);
        Geometry icon = Geometry.Combine(Circle(16, 16, 9.5), innerRightHalf, GeometryCombineMode.Exclude, null);

        for (int i = 0; i < 8; i++)
        {
            var ray = new RectangleGeometry(new Rect(14.7, 0.3, 2.6, 4.2), 1.3, 1.3, new RotateTransform(i * 45, 16, 16));
            icon = Union(icon, ray);
        }
        return Frozen(Scaled(icon, 1.12));
    }

    // Lua logo: planet with the inner moon and "Lua" cut out, plus the orbiting moon.
    private static Geometry BuildLua()
    {
        var center = new Point(14, 18);
        const double radius = 12.5;
        const double moonRadius = 3.4;
        double inner = 0.55 * radius / Math.Sqrt(2);
        double outer = 1.35 * radius / Math.Sqrt(2);

        var text = new FormattedText("Lua", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Verdana"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            0.7 * radius, Brushes.Black, 1.0);
        var label = text.BuildGeometry(new Point(center.X - text.Width / 2, center.Y + 0.42 * radius - text.Baseline));

        var body = Geometry.Combine(Circle(center.X, center.Y, radius), Circle(center.X + inner, center.Y - inner, moonRadius),
            GeometryCombineMode.Exclude, null);
        body = Geometry.Combine(body, label, GeometryCombineMode.Exclude, null);

        // The solid logo reads smaller than the outline icons, so fill a bit more of the grid.
        var logo = Union(body, Circle(center.X + outer, center.Y - outer, moonRadius));
        logo.Transform = new ScaleTransform(1.0625, 1.0625, 16, 16);
        return Frozen(logo);
    }

    // Person with a "no entry" badge in the bottom-right corner.
    private static Geometry BuildEnemy()
    {
        var badgeCenter = new Point(23.5, 23.5);
        Geometry badge = Geometry.Combine(Circle(badgeCenter.X, badgeCenter.Y, 6.5), Circle(badgeCenter.X, badgeCenter.Y, 4.6),
            GeometryCombineMode.Exclude, null);
        var slash = new RectangleGeometry(new Rect(badgeCenter.X - 1, badgeCenter.Y - 5, 2, 10), 0, 0,
            new RotateTransform(-45, badgeCenter.X, badgeCenter.Y));
        badge = Union(badge, slash);

        return Frozen(Union(PersonWithBadgeCutout(badgeCenter), badge));
    }

    // Person with a "+" in the bottom-right corner.
    private static Geometry BuildTeammate()
    {
        var badgeCenter = new Point(23.5, 23.5);
        var plus = Union(
            new RectangleGeometry(new Rect(badgeCenter.X - 5.5, badgeCenter.Y - 1.25, 11, 2.5)),
            new RectangleGeometry(new Rect(badgeCenter.X - 1.25, badgeCenter.Y - 5.5, 2.5, 11)));

        return Frozen(Union(PersonWithBadgeCutout(badgeCenter), plus));
    }

    // Head + shoulders, with room cleared around a corner badge so the badge reads clearly.
    private static Geometry PersonWithBadgeCutout(Point badgeCenter)
    {
        var shoulders = Geometry.Parse("M3,29 L3,25 C3,20 7.5,17.5 13.5,17.5 C19.5,17.5 24,20 24,25 L24,29 Z");
        var person = Union(Circle(13.5, 9.5, 6), shoulders);
        return Geometry.Combine(person, Circle(badgeCenter.X, badgeCenter.Y, 8.3), GeometryCombineMode.Exclude, null);
    }

    // Outline globe with a meridian, the equator and two latitude lines.
    private static Geometry BuildGlobe()
    {
        const double r = 12.5, stroke = 1.9;
        Geometry globe = Geometry.Combine(Circle(16, 16, r), Circle(16, 16, r - stroke), GeometryCombineMode.Exclude, null);

        var meridian = Geometry.Combine(new EllipseGeometry(new Point(16, 16), 6, r), new EllipseGeometry(new Point(16, 16), 6 - stroke, r - stroke),
            GeometryCombineMode.Exclude, null);
        Geometry lines = Union(meridian, new RectangleGeometry(new Rect(16 - stroke / 2, 3.5, stroke, 25)));
        lines = Union(lines, new RectangleGeometry(new Rect(3.5, 16 - stroke / 2, 25, stroke)));
        lines = Union(lines, new RectangleGeometry(new Rect(3.5, 9.5 - stroke / 2, 25, stroke)));
        lines = Union(lines, new RectangleGeometry(new Rect(3.5, 22.5 - stroke / 2, 25, stroke)));
        lines = Geometry.Combine(lines, Circle(16, 16, r - 0.5), GeometryCombineMode.Intersect, null);

        return Frozen(Union(globe, lines));
    }

    // Almond-shaped eye outline with a filled pupil.
    private static Geometry BuildEye()
    {
        var outer = Geometry.Parse("M1.5,16 C6,7.5 12,5 16,5 C20,5 26,7.5 30.5,16 C26,24.5 20,27 16,27 C12,27 6,24.5 1.5,16 Z");
        var inner = Geometry.Parse("M4.3,16 C8.2,9.3 13,7.2 16,7.2 C19,7.2 23.8,9.3 27.7,16 C23.8,22.7 19,24.8 16,24.8 C13,24.8 8.2,22.7 4.3,16 Z");
        var outline = Geometry.Combine(outer, inner, GeometryCombineMode.Exclude, null);

        return Frozen(Union(outline, Circle(16, 16, 5)));
    }

    // Frag grenade: round body, a fuze cap on top, a safety lever curving down the right side and a
    // pull ring on the upper-left.
    private static Geometry BuildGrenade()
    {
        var body = new EllipseGeometry(new Point(15.5, 20.5), 9, 9);
        // Fuze cap: a trapezoid narrowing towards the top, sitting on a thin collar.
        var cap = Geometry.Parse("M11.5,7 L19.5,7 L18.5,11.5 L12.5,11.5 Z");
        var collar = new RectangleGeometry(new Rect(10.5, 11, 10, 2));
        // Safety lever: a clean bar from the cap top, arcing right and down alongside the body.
        var lever = Geometry.Parse("M18.5,7.6 L23.5,7 C25,6.8 25.4,8.6 24.3,9.4 L20.5,21 L18.2,20.2 L21.8,9.6 L18.5,9.6 Z");
        var ringStem = new RectangleGeometry(new Rect(9, 8, 3.5, 2));
        var ring = Geometry.Combine(Circle(6, 8.5, 3.2), Circle(6, 8.5, 1.6), GeometryCombineMode.Exclude, null);

        var grenade = Union(body, cap);
        grenade = Union(grenade, collar);
        grenade = Union(grenade, lever);
        grenade = Union(grenade, ringStem);
        return Frozen(Scaled(Union(grenade, ring), 1.12));
    }

    // Suppressed pistol.
    private static Geometry BuildPistol() => Frozen(Shape(
        Box(10, 4, 24, 5),                  // slide
        Box(11, 3, 2.5, 1),                 // rear sight
        Box(31, 3, 1.5, 1),                 // front sight
        Box(34, 4.6, 22, 3.8),              // suppressor
        Box(18, 9, 16, 1.6),                // frame
        Poly("M11,9 L19,9 L17.5,19 L9,19 Z"),   // grip
        Guard(19, 9, 7, 4.6, 1.2)));

    private static Geometry BuildSmg() => Frozen(Shape(
        Poly("M1,4.5 L10,4.5 L10,10 L1,12.5 Z"), // stock
        Box(10, 4, 26, 6),                  // receiver
        Box(12, 2.8, 3, 1.2),               // rear sight
        Box(36, 4.8, 8, 5),                 // handguard
        Box(41, 2.8, 1.5, 2),               // front sight
        Box(44, 6, 8, 2),                   // barrel
        Poly("M28,10 L32,10 L35,17.5 L31,18.5 Z"), // magazine
        Poly("M15,10 L20,10 L18.5,17 L13.5,17 Z"), // grip
        Guard(20, 10, 6, 3.6, 1.2)));

    // AK-style rifle with a curved magazine.
    private static Geometry BuildRifle() => Frozen(Shape(
        Poly("M0,5.5 L12,7 L14,7 L14,10.5 L12,10.5 L0,14 Z"), // stock
        Box(14, 5, 20, 5.5),                // receiver
        Box(31, 3.8, 3, 1.2),               // rear sight
        Box(34, 4.3, 12, 1.7),              // gas tube
        Box(34, 6, 11, 4),                  // handguard
        Box(45, 6.8, 14, 1.8),              // barrel
        Box(54, 4.6, 1.5, 2.2),             // front sight
        Poly("M26,10.5 L31,10.5 L33.5,14 L36.5,18 L32,19.5 L28.5,15 Z"), // magazine
        Poly("M18,10.5 L22,10.5 L20.5,17 L16.5,17 Z"),                   // grip
        Guard(22, 10.5, 4.5, 3, 1.1)));

    // Pump shotgun.
    private static Geometry BuildShotgun() => Frozen(Shape(
        Poly("M0,6 L13,6.5 L13,10.5 L0,12.5 Z"), // stock
        Box(13, 5.5, 13, 5),                // receiver
        Box(26, 5.5, 32, 1.8),              // barrel
        Box(26, 7.8, 24, 1.4),              // magazine tube
        Box(33, 7.2, 11, 3.6),              // pump
        Box(56.5, 4.5, 1, 1),               // bead
        Guard(15, 10.5, 6, 3.4, 1.2)));

    // Belt-fed machine gun with a box magazine and bipod.
    private static Geometry BuildMachineGun() => Frozen(Shape(
        Poly("M0,5 L10,5 L12,6 L12,10.5 L0,12 Z"), // stock
        Box(12, 4, 24, 6.5),                // receiver
        Box(16, 2.8, 16, 1.2),              // feed cover
        Box(36, 4.8, 10, 4.4),              // heat shield
        Box(46, 5.8, 13, 1.8),              // barrel
        Box(22, 10.5, 10, 6.5),             // box magazine
        Poly("M13.5,10.5 L17.5,10.5 L16.5,16.5 L12.5,16.5 Z"), // grip
        Guard(17.5, 10.5, 4, 3, 1.1),
        Poly("M48.5,7.6 L50,7.6 L46.5,17.5 L45,17.5 Z"),        // bipod
        Poly("M50,7.6 L51.5,7.6 L55,17.5 L53.5,17.5 Z")));

    // Bolt-action sniper rifle with scope.
    private static Geometry BuildSniper() => Frozen(Shape(
        Poly("M0,6.5 L5,6.5 L9,8.5 L16,8.5 L16,12 L9,12 L4,14 L0,14 Z"), // stock
        Box(16, 7.5, 18, 4.5),              // body
        Box(34, 8.5, 22, 1.8),              // barrel
        Box(55, 7.8, 4, 3.2),               // muzzle brake
        Box(19, 3.6, 17, 2.2),              // scope tube
        Poly("M35,3.6 L38,2.4 L41,2.4 L41,7 L38,7 L35,5.8 Z"), // objective bell
        Box(16.5, 2.8, 3, 3.8),             // eyepiece
        Box(22, 5.8, 2, 1.7),               // mounts
        Box(30, 5.8, 2, 1.7),
        Poly("M20,12 L24,12 L22.5,17.5 L18.5,17.5 Z"),   // grip
        Guard(24, 12, 4.5, 3, 1.1),
        Box(29, 12, 4, 2.5)));              // magazine

    private static Geometry Shape(params Geometry[] parts)
    {
        var shape = parts[0];
        for (int i = 1; i < parts.Length; i++) shape = Union(shape, parts[i]);
        return shape;
    }

    private static Geometry Box(double x, double y, double width, double height) =>
        new RectangleGeometry(new Rect(x, y, width, height));

    private static Geometry Poly(string pathData) => Geometry.Parse(pathData);

    // U-shaped trigger guard hanging under the receiver (open at the top).
    private static Geometry Guard(double x, double y, double width, double height, double thickness) =>
        Geometry.Combine(Box(x, y, width, height), Box(x + thickness, y, width - 2 * thickness, height - thickness),
            GeometryCombineMode.Exclude, null);

    private static EllipseGeometry Circle(double x, double y, double r) => new(new Point(x, y), r, r);

    private static Geometry Union(Geometry a, Geometry b) => Geometry.Combine(a, b, GeometryCombineMode.Union, null);

    // Scales a finished icon around the grid center, to fine-tune its visual size vs. the glyph icons.
    private static Geometry Scaled(Geometry geometry, double factor)
    {
        geometry.Transform = new ScaleTransform(factor, factor, 16, 16);
        return geometry;
    }

    private static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }
}
