using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using BrakeDiscInspector_GUI_ROI.Workflow;


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

            if (!TryGetBackendDataset(out var roleId, out var baseRoiId, out var mmPerPx))
            { Snack("Configura Role/ROI en Dataset & AI antes de analizar"); return; }

            var inferM1 = await BackendAPI.InferAsync(
                _currentImagePathWin,
                _layout.Master1Pattern,
                roleId,
                ResolveBackendRoiId(_layout.Master1Pattern, baseRoiId, "Master1Pattern"),
                mmPerPx,
                AppendLog);

            if (!inferM1.ok || inferM1.result == null)
            {
                Snack("No se obtuvo inferencia para Master 1" + (inferM1.error != null ? $" ({inferM1.error})" : ""));
                return;
            }

            var inferM2 = await BackendAPI.InferAsync(
                _currentImagePathWin,
                _layout.Master2Pattern,
                roleId,
                ResolveBackendRoiId(_layout.Master2Pattern, baseRoiId, "Master2Pattern"),
                mmPerPx,
                AppendLog);

            if (!inferM2.ok || inferM2.result == null)
            {
                Snack("No se obtuvo inferencia para Master 2" + (inferM2.error != null ? $" ({inferM2.error})" : ""));
                return;
            }

            var resp1 = inferM1.result.Response;
            var resp2 = inferM2.result.Response;
            AppendLog($"[infer] M1 score={resp1.score:0.###} thr={resp1.threshold:0.###}");
            AppendLog($"[infer] M2 score={resp2.score:0.###} thr={resp2.threshold:0.###}");

            var center1 = _layout.Master1Pattern.GetCenter();
            var center2 = _layout.Master2Pattern.GetCenter();
            var c1 = new System.Windows.Point(center1.cx, center1.cy);
            var c2 = new System.Windows.Point(center2.cx, center2.cy);
            var mid = new System.Windows.Point((c1.X + c2.X) / 2.0, (c1.Y + c2.Y) / 2.0);
            var (c1Canvas, c2Canvas, midCanvas) = ConvertMasterPointsToCanvas(c1, c2, mid);

            if (_layout.Inspection == null) { Snack("Falta ROI de Inspección"); return; }
            MoveInspectionTo(_layout.Inspection, c1, c2);
            ClipInspectionROI(_layout.Inspection, _imgW, _imgH);

            RedrawOverlay();
            DrawCross(c1Canvas.X, c1Canvas.Y, 20, Brushes.LimeGreen, 2);
            DrawCross(c2Canvas.X, c2Canvas.Y, 20, Brushes.Orange, 2);
            DrawCross(midCanvas.X, midCanvas.Y, 24, Brushes.Red, 2);

            Snack($"Masters OK. Scores: M1={resp1.score:0.000}{FormatThreshold(resp1.threshold)}, M2={resp2.score:0.000}{FormatThreshold(resp2.threshold)}");
        }


        private (System.Windows.Point c1Canvas, System.Windows.Point c2Canvas, System.Windows.Point midCanvas) ConvertMasterPointsToCanvas(
            System.Windows.Point c1,
            System.Windows.Point c2,
            System.Windows.Point mid)
        {
            var c1Canvas = ImagePxToCanvasPt(c1.X, c1.Y);
            var c2Canvas = ImagePxToCanvasPt(c2.X, c2.Y);
            var midCanvas = ImagePxToCanvasPt(mid.X, mid.Y);

            return (c1Canvas, c2Canvas, midCanvas);
        }


        public async Task AnalyzeInspectionViaBackend()
        {
            if (_layout?.Inspection == null) { Snack("Falta ROI de Inspección"); return; }
            if (string.IsNullOrWhiteSpace(_currentImagePathWin) || !File.Exists(_currentImagePathWin))
            { Snack("No hay imagen cargada"); return; }

            if (!TryGetBackendDataset(out var roleId, out var roiId, out var mmPerPx))
            { Snack("Configura Role/ROI en Dataset & AI antes de analizar"); return; }

            var result = await BackendAPI.InferAsync(
                _currentImagePathWin,
                _layout.Inspection,
                roleId,
                roiId,
                mmPerPx,
                AppendLog);

            if (!result.ok || result.result == null)
            {
                Snack(result.error ?? "Error en /infer");
                return;
            }

            var resp = result.result.Response;
            var label = resp.threshold > 0 && resp.score <= resp.threshold ? "OK" : "NG";
            Snack($"Resultado backend: {label} (score={resp.score:0.###}, thr={resp.threshold:0.###})");
            UpdateResultLabel(label, resp);

            if (!string.IsNullOrWhiteSpace(resp.heatmap_png_base64))
            {
                try
                {
                    var heatBytes = Convert.FromBase64String(resp.heatmap_png_base64);
                    var export = new Workflow.RoiExportResult(result.result.PngBytes, result.result.ShapeJson, result.result.RoiImage);
                    await ShowHeatmapOverlayAsync(export, heatBytes, _heatmapOverlayOpacity);
                }
                catch (FormatException ex)
                {
                    AppendLog("[infer] heatmap decode error: " + ex.Message);
                }
            }
            else
            {
                ClearHeatmapOverlay();
            }
        }
    }
}