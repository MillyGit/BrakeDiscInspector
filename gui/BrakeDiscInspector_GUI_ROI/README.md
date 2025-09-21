# BrakeDiscInspector_GUI_ROI

## 1. Objetivo del sistema
Sistema de inspección visual de discos de freno mediante GUI en C# WPF y backend en Python (Flask + OpenCV/TensorFlow). Permite definir ROIs, validar patrones maestros y ejecutar inspección de defectos.

---

## 2. Flujo de uso
1. Cargar imagen BMP en el GUI.
2. Dibujar ROIs: Master1 Pattern, Master1 Search, Master2 Pattern, Master2 Search, ROI de Inspección.
3. Guardar layout y preset.
4. Validar masters: matcher local o backend.
5. Analizar inspección: mover ROI según masters y enviar a backend `/analyze`.
6. Mostrar resultados (cruces, logs, estado).

---

## 3. Estructura del GUI (C#)
- **MainWindow.xaml / .cs**: control principal de la UI y flujo de análisis.
- **MainWindow.cs**: código parcial auxiliar.
- **BackendAPI.cs**: comunicación HTTP con Flask (MatchMasterAsync → `/match_master` alias `/match_one`, AnalyzeAsync, TryCropToPng).
- **LocalMatcher.cs**: coincidencia local con OpenCVSharp.
- **RoiAdorner.cs / RoiOverlay.cs**: dibujo y manipulación de ROIs.
- **PresetManager.cs**: gestión de parámetros de coincidencia.
- **MasterLayout.cs**: persistencia de ROIs y layouts.
- **AssemblyInfo.cs**: metadatos del ensamblado.

---

## 4. Backend (Python)
- **app.py**: servidor Flask con endpoints:
  - `/match_master` (alias `/match_one`)
  - `/analyze`
  - `/train_status`
- **matcher.py**: lógica de matching ORB/SIFT.
- **model.py**: TensorFlow (EfficientNetB3) para inspección de defectos.

---

## 5. Problemas comunes resueltos
- `.center` en `MatchOneResult` eliminado → usar `x/y`.
- `TryCropToPng` con `out MemoryStream, out string`.
- Conversión `double→int` → `Math.Round`.
- Logs depurados.
- Timeout backend → revisar JSON esperado.

---

## 6. Archivos necesarios
1. MainWindow.xaml.cs  
2. MainWindow.cs  
3. MainWindow.xaml.txt (si no abre bien el XAML)  
4. BackendAPI.cs  
5. LocalMatcher.cs  
6. RoiAdorner.cs  
7. RoiOverlay.cs  
8. PresetManager.cs  
9. MasterLayout.cs  
10. AssemblyInfo.cs  
11. BrakeDiscInspector_GUI_ROI.csproj.txt  
12. BrakeDiscInspector_GUI_ROI.sln.txt  
13. all_snippets.txt  
14. Backend: app.py, matcher.py, model.py, data de validación

---

## 7. Notas finales
- Usar siempre la última versión de BackendAPI.cs.
- Confirmar que el JSON del backend contiene `found`, `center_x`, `center_y`, `confidence`, `score`, `scale`.
- Verificar conectividad entre Windows y WSL (0.0.0.0 vs 127.0.0.1).

---
