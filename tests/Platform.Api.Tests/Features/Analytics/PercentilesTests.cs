using Platform.Api.Features.Analytics;

namespace Platform.Api.Tests.Features.Analytics;

public class PercentilesTests
{
    [Fact]
    public void Compute_EmptyInput_ReturnsNull()
    {
        Assert.Null(Percentiles.Compute([], 0.5));
        Assert.Null(Percentiles.Median([]));
    }

    [Fact]
    public void Compute_SingleValue_ReturnsIt()
    {
        Assert.Equal(7.0, Percentiles.Compute([7.0], 0.5));
        Assert.Equal(7.0, Percentiles.Compute([7.0], 0.9));
    }

    [Fact]
    public void Median_OddCount_ReturnsMiddle()
    {
        Assert.Equal(3.0, Percentiles.Median([5.0, 1.0, 3.0]));
    }

    [Fact]
    public void Median_EvenCount_Interpolates()
    {
        Assert.Equal(2.5, Percentiles.Median([1.0, 2.0, 3.0, 4.0]));
    }

    [Fact]
    public void Compute_InterpolatesBetweenRanks()
    {
        // numpy.percentile([10, 20, 30, 40], 90) == 37.0
        Assert.Equal(37.0, Percentiles.Compute([10.0, 20.0, 30.0, 40.0], 0.9)!.Value, precision: 6);
    }

    [Fact]
    public void Compute_ClampsExtremes()
    {
        var values = new[] { 3.0, 1.0, 2.0 };
        Assert.Equal(1.0, Percentiles.Compute(values, 0));
        Assert.Equal(3.0, Percentiles.Compute(values, 1));
    }

    [Fact]
    public void Compute_DoesNotMutateInput()
    {
        var values = new List<double> { 3.0, 1.0, 2.0 };
        Percentiles.Compute(values, 0.5);
        Assert.Equal([3.0, 1.0, 2.0], values);
    }
}
