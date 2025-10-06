using BrakeDiscInspector_GUI_ROI;
using Xunit;

namespace BrakeDiscInspector_GUI_ROI.Tests;

public class AnnulusDefaultsTests
{
    [Theory]
    [InlineData(double.NaN, 60.0, 10.0)]
    [InlineData(-5.0, 60.0, 10.0)]
    [InlineData(0.0, 60.0, 10.0)]
    [InlineData(4.0, 60.0, 10.0)]
    [InlineData(12.0, 60.0, 12.0)]
    [InlineData(80.0, 60.0, 60.0)]
    public void ClampInnerRadius_HonorsMinimumWhenOuterAllows(double requested, double outer, double expected)
    {
        double clamped = AnnulusDefaults.ClampInnerRadius(requested, outer);
        Assert.Equal(expected, clamped, precision: 6);
    }

    [Theory]
    [InlineData(0.0, 5.0, 0.0)]
    [InlineData(3.0, 5.0, 3.0)]
    [InlineData(7.0, 5.0, 5.0)]
    public void ClampInnerRadius_AllowsSmallerAnnulusWhenOuterTooSmall(double requested, double outer, double expected)
    {
        double clamped = AnnulusDefaults.ClampInnerRadius(requested, outer);
        Assert.Equal(expected, clamped, precision: 6);
    }
}
