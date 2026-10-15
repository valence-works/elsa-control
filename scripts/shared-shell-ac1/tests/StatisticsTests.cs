namespace ElsaControl.SharedShellAc1.Tests;

public sealed class StatisticsTests
{
    [Fact]
    public void Percentile_interpolates_between_sorted_samples()
    {
        double[] values = [1, 2, 3, 4, 5];
        Assert.Equal(1, MetricSummary.Percentile(values, 0));
        Assert.Equal(3, MetricSummary.Percentile(values, 50));
        Assert.Equal(4.8, MetricSummary.Percentile(values, 95), precision: 6);
        Assert.Equal(5, MetricSummary.Percentile(values, 100));
    }

    [Fact]
    public void From_reports_mean_and_extrema()
    {
        var summary = MetricSummary.From([10, 20, 30]);
        Assert.Equal(20, summary.Mean);
        Assert.Equal(10, summary.Min);
        Assert.Equal(30, summary.Max);
        Assert.Equal(3, summary.Count);
    }
}
