namespace Aetheria.Shared.Math;

/// <summary>
/// A 2D position/vector on the world ground plane (X = east/west, Y = north/south).
/// Isometric projection is purely a client rendering concern — the server simulates
/// on a flat plane. Uses <see cref="float"/> for the walking skeleton; if lockstep or
/// cross-platform determinism is ever needed, swap this for a fixed-point type behind
/// the same surface.
/// </summary>
public readonly struct Vec2 : IEquatable<Vec2>
{
    public readonly float X;
    public readonly float Y;

    public Vec2(float x, float y)
    {
        X = x;
        Y = y;
    }

    public static Vec2 Zero => new(0f, 0f);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, float s) => new(a.X * s, a.Y * s);

    public float LengthSquared => (X * X) + (Y * Y);
    public float Length => MathF.Sqrt(LengthSquared);

    /// <summary>Returns a unit-length vector, or <see cref="Zero"/> if this is (near) zero.</summary>
    public Vec2 Normalized()
    {
        float len = Length;
        return len > 1e-6f ? new Vec2(X / len, Y / len) : Zero;
    }

    public static float DistanceSquared(Vec2 a, Vec2 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    public bool Equals(Vec2 other) => X.Equals(other.X) && Y.Equals(other.Y);
    public override bool Equals(object? obj) => obj is Vec2 other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public override string ToString() => $"({X:0.##}, {Y:0.##})";

    public static bool operator ==(Vec2 left, Vec2 right) => left.Equals(right);
    public static bool operator !=(Vec2 left, Vec2 right) => !left.Equals(right);
}
