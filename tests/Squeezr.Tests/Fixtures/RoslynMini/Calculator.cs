namespace Mini;

/// <summary>
/// Simple arithmetic helpers.
/// </summary>
public class Calculator
{
    /// <summary>
    /// Adds two integers.
    /// </summary>
    /// <param name="a">The first summand.</param>
    /// <param name="b">The second summand.</param>
    /// <returns>The sum of <paramref name="a"/> and <paramref name="b"/>.</returns>
    public int Add(int a, int b)
    {
        return a + b;
    }

    /// <summary>
    /// Wraps a value into a single-element array.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="value">The value to wrap.</param>
    /// <returns>An array containing <paramref name="value"/>.</returns>
    public T[] Wrap<T>(T value)
    {
        return new[] { value };
    }
}
