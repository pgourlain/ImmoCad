namespace ImmoCad.Core.Model;

/// <summary>2D point in millimetres.</summary>
public readonly record struct PointMm(int X, int Y);

/// <summary>3D point in millimetres (Z up).</summary>
public readonly record struct PointMm3(int X, int Y, int Z);
