using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;


namespace BrakeDiscInspector_GUI_ROI
{
    public partial class MainWindow
    {
        public async Task AnalyzeMastersViaBackend()
        {
            if (_layout?.Master1Pattern == null || _layout?.Master2Pattern == null)
            { Snack("Faltan ROIs de patrón para Master 1/2"); return; }
            if (string.IsNullOrWhiteSpace(_currentImagePathWin) || !File.Exists(_currentImagePathWin))
            { Snack("No hay imagen cargada"); return; }


            var tplM1 = RoiToRect(_layout.Master1Pattern);
            var tplM2 = RoiToRect(_layout.Master2Pattern);


            var r1 = await BackendAPI.MatchOneViaFilesAsync(
            _currentImagePathWin, tplM1,
            _preset.MatchThr, _preset.RotRange, _preset.ScaleMin, _preset.ScaleMax,
            string.IsNullOrWhiteSpace(_preset.Feature) ? "auto" : _preset.Feature,
            0.8, false, "M1", AppendLog);


            var r2 = await BackendAPI.MatchOneViaFilesAsync(
            _currentImagePathWin, tplM2,
            _preset.MatchThr, _preset.RotRange, _preset.ScaleMin, _preset.ScaleMax,
            string.IsNullOrWhiteSpace(_preset.Feature) ? "auto" : _preset.Feature,
            0.8, false, "M2", AppendLog);


            if (!r1.ok || r1.center == null) { Snack("No se encontró Master 1" + (r1.error != null ? $" ({r1.error})" : "")); return; }
            if (!r2.ok || r2.center == null) { Snack("No se encontró Master 2" + (r2.error != null ? $" ({r2.error})" : "")); return; }


            var c1 = r1.center.Value; var c2 = r2.center.Value;
            var mid = new System.Windows.Point((c1.X + c2.X) / 2.0, (c1.Y + c2.Y) / 2.0);


            if (_layout.Inspection == null) { Snack("Falta ROI de Inspección"); return; }
            MoveInspectionTo(_layout.Inspection, mid.X, mid.Y);
            ClipInspectionROI(_layout.Inspection, _imgW, _imgH);


            RedrawOverlay();
            DrawCross(c1.X, c1.Y, 20, Brushes.LimeGreen, 2);
            DrawCross(c2.X, c2.Y, 20, Brushes.Orange, 2);
            DrawCross(mid.X, mid.Y, 24, Brushes.Red, 2);
        }


        public async Task AnalyzeInspectionViaBackend()
        {
            if (_layout?.Inspection == null) { Snack("Falta ROI de Inspección"); return; }
            if (string.IsNullOrWhiteSpace(_currentImagePathWin) || !File.Exists(_currentImagePathWin))
            { Snack("No hay imagen cargada"); return; }


            var inspRect = RoiToRect(_layout.Inspection);
            var resp = await BackendAPI.AnalyzeAsync(_currentImagePathWin, inspRect, _preset, AppendLog);
            if (!resp.ok) { Snack("Analyze backend: " + (resp.error ?? "error desconocido")); return; }
            Snack($"Resultado: {resp.label} (score={resp.score:0.000})");
        }
    }
}