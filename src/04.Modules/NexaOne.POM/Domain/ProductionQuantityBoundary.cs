namespace NexaOne.POM.Domain;

/// <summary>Shared SQL DECIMAL(18,4) boundary for production quantities.</summary>
public static class ProductionQuantityBoundary
{
    public const decimal MaxValue = 99999999999999.9999m;

    public static bool Fits(decimal value)
        => value >= -MaxValue && value <= MaxValue && decimal.Round(value, 4) == value;

    /// <summary>
    /// Adds two production quantities without allowing a CLR decimal overflow or a value that
    /// cannot be stored in the shared SQL DECIMAL(18,4) columns.
    /// </summary>
    public static bool TryAdd(decimal left, decimal right, out decimal result)
    {
        result = 0m;
        if (!Fits(left) || !Fits(right)) return false;

        try
        {
            result = left + right;
        }
        catch (OverflowException)
        {
            return false;
        }

        return Fits(result);
    }
}
