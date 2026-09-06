namespace Mini;

public static class Program
{
    public static void Main()
    {
        var calculator = new Calculator();
        var sum = calculator.Add(1, 2);
        IShape shape = new Circle(1.5);
        var other = new Square(2);
        var total = shape.Area + other.Area + sum;
        Console.WriteLine(total);
    }
}
