namespace Mini;

/// <summary>
/// A shape that can report its area.
/// </summary>
public interface IShape
{
    /// <summary>Gets the area of the shape.</summary>
    double Area { get; }
}

/// <summary>
/// Base class for all shapes.
/// </summary>
public abstract class ShapeBase
{
}

/// <summary>
/// A circle defined by its radius.
/// </summary>
/// <param name="Radius">The radius of the circle.</param>
public record Circle(double Radius) : IShape
{
    /// <inheritdoc />
    public double Area => Math.PI * Radius * Radius;
}

/// <summary>
/// A square with equal sides.
/// </summary>
public class Square : ShapeBase, IShape
{
    /// <summary>Initializes a square with the given side length.</summary>
    /// <param name="side">Length of one side.</param>
    public Square(double side)
    {
        Side = side;
    }

    /// <inheritdoc />
    public double Area => Side * Side;

    /// <summary>Gets the side length.</summary>
    public double Side { get; }
}
