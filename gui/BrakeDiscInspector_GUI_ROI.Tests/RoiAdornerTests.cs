using System;
using System.Reflection;
using System.Windows.Controls;
using BrakeDiscInspector_GUI_ROI;
using Xunit;

namespace BrakeDiscInspector_GUI_ROI.Tests;

public class RoiAdornerTests
{
    [StaFact]
    public void ResizeByCorner_KeepsAnnulusUniformAndClampsInnerRadius()
    {
        var roi = new RoiModel
        {
            Shape = RoiShape.Annulus,
            CX = 60,
            CY = 60,
            Width = 120,
            Height = 120,
            R = 60,
            RInner = 30
        };

        var annulus = new AnnulusShape
        {
            Width = 120,
            Height = 120,
            InnerRadius = 30,
            Tag = roi
        };

        Canvas.SetLeft(annulus, 0);
        Canvas.SetTop(annulus, 0);

        var overlay = new RoiOverlay();
        var adorner = new RoiAdorner(annulus, overlay, (_, _) => { }, _ => { });

        InvokeResizeByCorner(adorner, 20.0, -20.0, "NE");

        Assert.True(Math.Abs(annulus.Width - annulus.Height) < 1e-6);
        Assert.True(Math.Abs(roi.Width - roi.Height) < 1e-6);

        annulus.InnerRadius = annulus.Width / 2.0 + 5.0;

        InvokeResizeByCorner(adorner, -40.0, 40.0, "NE");

        Assert.True(Math.Abs(annulus.Width - annulus.Height) < 1e-6);
        Assert.True(Math.Abs(roi.Width - roi.Height) < 1e-6);
        Assert.True(annulus.InnerRadius <= annulus.Width / 2.0 + 1e-6);
    }

    private static void InvokeResizeByCorner(RoiAdorner adorner, double dx, double dy, string cornerName)
    {
        var method = typeof(RoiAdorner).GetMethod("ResizeByCorner", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ResizeByCorner not found");

        var cornerType = typeof(RoiAdorner).GetNestedType("Corner", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Corner enum not found");

        object corner = Enum.Parse(cornerType, cornerName);

        method.Invoke(adorner, new[] { (object)dx, (object)dy, corner });
    }
}
