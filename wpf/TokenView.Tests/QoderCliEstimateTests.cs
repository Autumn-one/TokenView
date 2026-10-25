using TokenView.Wpf.Data.Usage;
using Xunit;

namespace TokenView.Tests;

public sealed class QoderCliEstimateTests
{
    [Fact]
    public void ContextRatioAndContentProduceInputAndOutputEstimates()
    {
        var estimate = QoderCliEstimate.Compute(0.25, 9);

        Assert.NotNull(estimate);
        Assert.Equal(50_000, estimate.Value.Input);
        Assert.Equal(3, estimate.Value.Output);
        Assert.Equal(0, estimate.Value.CacheRead);
        Assert.Equal(0, estimate.Value.CacheWrite);
        Assert.Null(estimate.Value.Reasoning);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-0.1d)]
    [InlineData(1.1d)]
    [InlineData(double.NaN)]
    public void MissingOrInvalidRatioStaysUnknown(double? ratio)
    {
        Assert.Null(QoderCliEstimate.Compute(ratio, 100));
    }

    [Fact]
    public void EmptyOutputStillProducesInputEstimate()
    {
        var estimate = QoderCliEstimate.Compute(0.5, 0);

        Assert.NotNull(estimate);
        Assert.Equal(100_000, estimate.Value.Input);
        Assert.Equal(0, estimate.Value.Output);
    }
}
