namespace RepoWatch.Core.State;

/// <summary>
/// A count that knows whether it is exact. A first page of results is a lower bound,
/// not a total, and must be labeled as such (e.g. "30+").
/// </summary>
public readonly record struct ItemCount(int Value, bool IsExact)
{
    public static ItemCount Exact(int value) => new(value, true);

    public static ItemCount AtLeast(int value) => new(value, false);

    public override string ToString() => IsExact ? Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{Value}+";
}
