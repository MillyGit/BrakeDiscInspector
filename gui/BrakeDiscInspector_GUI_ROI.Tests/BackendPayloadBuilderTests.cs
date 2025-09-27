using System;
using System.Reflection;
using System.Text.Json;
using BrakeDiscInspector_GUI_ROI;
using Xunit;

namespace BrakeDiscInspector_GUI_ROI.Tests;

public class BackendPayloadBuilderTests
{
    [Fact]
    public void SerializeRoiToJson_EmitsCenterCoordinates()
    {
        var roi = new RoiModel
        {
            Shape = RoiShape.Rectangle,
            Width = 48,
            Height = 32,
            AngleDeg = 37.5
        };
        roi.Left = 120;
        roi.Top = 80;

        string json = InvokeSerializeRoiToJson(roi);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(roi.X, root.GetProperty("x").GetDouble());
        Assert.Equal(roi.Y, root.GetProperty("y").GetDouble());
        Assert.Equal(roi.AngleDeg, root.GetProperty("angle_deg").GetDouble());
    }

    [Fact]
    public void SerializeRoiToJson_AnnulusIncludesInnerRadius()
    {
        var roi = new RoiModel
        {
            Shape = RoiShape.Annulus,
            CX = 180,
            CY = 120,
            R = 48,
            RInner = 20,
            AngleDeg = 15
        };

        string json = InvokeSerializeRoiToJson(roi);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("annulus", root.GetProperty("shape").GetString());
        Assert.Equal(roi.R, root.GetProperty("r").GetDouble());
        Assert.Equal(roi.RInner, root.GetProperty("ri").GetDouble());
    }

    [Fact]
    public void TryGetSearchRect_UsesLeftTopForRectangle()
    {
        var roi = new RoiModel
        {
            Shape = RoiShape.Rectangle,
            Width = 60,
            Height = 40
        };
        roi.Left = 50;
        roi.Top = 30;

        object[] args = { roi, 0, 0, 0, 0 };
        bool ok = (bool)TryGetSearchRectMethod().Invoke(null, args)!;

        Assert.True(ok);
        Assert.Equal((int)Math.Round(roi.Left), (int)args[1]);
        Assert.Equal((int)Math.Round(roi.Top), (int)args[2]);
        Assert.Equal((int)Math.Round(roi.Width), (int)args[3]);
        Assert.Equal((int)Math.Round(roi.Height), (int)args[4]);
    }

    private static string InvokeSerializeRoiToJson(RoiModel roi)
    {
        return (string)SerializeRoiToJsonMethod().Invoke(null, new object[] { roi })!;
    }

    private static MethodInfo SerializeRoiToJsonMethod()
    {
        return typeof(MainWindow).GetMethod("SerializeRoiToJson", BindingFlags.NonPublic | BindingFlags.Static)
               ?? throw new InvalidOperationException("SerializeRoiToJson not found");
    }

    private static MethodInfo TryGetSearchRectMethod()
    {
        return typeof(BackendAPI).GetMethod("TryGetSearchRect", BindingFlags.NonPublic | BindingFlags.Static)
               ?? throw new InvalidOperationException("TryGetSearchRect not found");
    }
}
