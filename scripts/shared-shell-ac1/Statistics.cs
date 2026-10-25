namespace ElsaControl.SharedShellAc1;

internal sealed record MetricSummary(double Mean, double P50, double P95, double Min, double Max, int Count)
{
    public static MetricSummary From(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            throw new ArgumentException("Cannot summarise an empty series.", nameof(values));

        var sorted = values.OrderBy(v => v).ToArray();
        return new MetricSummary(
            Mean: sorted.Average(),
            P50: Percentile(sorted, 50),
            P95: Percentile(sorted, 95),
            Min: sorted[0],
            Max: sorted[^1],
            Count: sorted.Length);
    }

    public static double Percentile(IReadOnlyList<double> sortedAscending, double percentile)
    {
        if (sortedAscending.Count == 0)
            throw new ArgumentException("Cannot compute a percentile of an empty series.", nameof(sortedAscending));
        if (sortedAscending.Count == 1)
            return sortedAscending[0];

        var index = (percentile / 100d) * (sortedAscending.Count - 1);
        var lo = (int)Math.Floor(index);
        var hi = (int)Math.Ceiling(index);
        if (lo == hi)
            return sortedAscending[lo];

        var weight = index - lo;
        return sortedAscending[lo] + ((sortedAscending[hi] - sortedAscending[lo]) * weight);
    }
}
