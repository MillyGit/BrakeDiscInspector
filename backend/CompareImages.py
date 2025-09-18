#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
compare_images_gui.py
Herramienta GUI para comparar ORIGINAL vs MASTER.

- Selectores de archivo (browser) y carpeta de salida.
- Encaje: LETTERBOX (mantiene aspecto) o FILL (deforma).
- Detección de máscara (alfa o fondo negro).
- Métricas: MSE, PSNR, SSIM (si scikit-image está instalado).
- Salidas: imágenes alineadas, máscara usada, diff global y diff ROI.
"""

import os
from pathlib import Path
import tkinter as tk
from tkinter import ttk, filedialog, messagebox

import numpy as np
import cv2

# SSIM opcional
try:
    from skimage.metrics import structural_similarity as ssim
    _HAS_SKIMAGE = True
except Exception:
    _HAS_SKIMAGE = False


# ================== Utilidades ==================

def robust_imread_any(path):
    data = np.fromfile(str(path), dtype=np.uint8)
    return cv2.imdecode(data, cv2.IMREAD_UNCHANGED)

def to_bgr(img):
    if img.ndim == 2:
        return cv2.cvtColor(img, cv2.COLOR_GRAY2BGR)
    if img.shape[2] == 4:
        return img[:, :, :3]
    return img

def letterbox_strict_keep_aspect(img, target_hw, border_color=(0,0,0), interpolation=cv2.INTER_CUBIC):
    th, tw = target_hw
    ih, iw = img.shape[:2]
    scale = min(tw / iw, th / ih)
    nw, nh = max(1, int(round(iw * scale))), max(1, int(round(ih * scale)))
    resized = cv2.resize(img, (nw, nh), interpolation=interpolation)

    if img.ndim == 3:
        canvas = np.full((th, tw, img.shape[2]), border_color, dtype=img.dtype)
        canvas[(th - nh)//2:(th - nh)//2 + nh, (tw - nw)//2:(tw - nw)//2 + nw, :] = resized
    else:
        canvas = np.zeros((th, tw), dtype=img.dtype)
        canvas[(th - nh)//2:(th - nh)//2 + nh, (tw - nw)//2:(tw - nw)//2 + nw] = resized

    return canvas, (scale, (tw - nw)//2, (th - nh)//2)

def letterbox_mask(mask, target_hw):
    th, tw = target_hw
    ih, iw = mask.shape[:2]
    scale = min(tw / iw, th / ih)
    nw, nh = max(1, int(round(iw * scale))), max(1, int(round(ih * scale)))
    resized = cv2.resize(mask, (nw, nh), interpolation=cv2.INTER_NEAREST)
    canvas = np.zeros((th, tw), dtype=np.uint8)
    top, left = (th - nh)//2, (tw - nw)//2
    canvas[top:top+nh, left:left+nw] = resized
    _, canvas = cv2.threshold(canvas, 128, 255, cv2.THRESH_BINARY)
    return canvas, (scale, left, top)

def fill_resize(img, target_hw, interpolation=cv2.INTER_AREA):
    th, tw = target_hw
    return cv2.resize(img, (tw, th), interpolation=interpolation), (None, 0, 0)

def compute_mask_from_master(master_raw, black_thresh=10, min_area_ratio=0.02):
    mask = None
    h, w = master_raw.shape[:2]
    if master_raw.ndim == 3 and master_raw.shape[2] == 4:
        alpha = master_raw[:, :, 3]
        mask = (alpha > 0).astype(np.uint8) * 255
    else:
        gray = master_raw if master_raw.ndim == 2 else cv2.cvtColor(master_raw, cv2.COLOR_BGR2GRAY)
        _, mask = cv2.threshold(gray, black_thresh, 255, cv2.THRESH_BINARY)

    area = int((mask > 0).sum())
    area_ratio = area / float(h*w)
    if area_ratio < min_area_ratio:
        cnts, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        if cnts:
            c = max(cnts, key=cv2.contourArea)
            mask2 = np.zeros_like(mask)
            cv2.drawContours(mask2, [c], -1, 255, thickness=cv2.FILLED)
            mask = mask2
            area = int((mask > 0).sum())
            area_ratio = area / float(h*w)
    if area_ratio < 0.002:
        return None, {"area": area, "ratio": area_ratio, "note": "mask_too_small"}
    return mask, {"area": area, "ratio": area_ratio, "note": "ok"}

def mse(a, b, mask=None):
    diff = (a.astype(np.float32) - b.astype(np.float32)) ** 2
    if mask is not None:
        m = (mask > 0)
        if m.sum() < 1: return float("nan")
        return float(diff[m].mean())
    return float(diff.mean())

def psnr(a, b, mask=None, data_range=255.0):
    _mse = mse(a, b, mask)
    if np.isnan(_mse) or _mse <= 1e-12:
        return float('inf') if _mse <= 1e-12 else float('nan')
    return float(10.0 * np.log10((data_range**2)/_mse))

def compute_ssim(a, b, mask=None):
    if not _HAS_SKIMAGE: return None
    if a.ndim == 3: a = cv2.cvtColor(a, cv2.COLOR_BGR2GRAY)
    if b.ndim == 3: b = cv2.cvtColor(b, cv2.COLOR_BGR2GRAY)
    if mask is not None:
        ys, xs = np.where(mask > 0)
        if len(xs) < 4 or len(ys) < 4: return None
        x0,x1,y0,y1 = xs.min(), xs.max()+1, ys.min(), ys.max()+1
        a = a[y0:y1, x0:x1]; b = b[y0:y1, x0:x1]
    return float(ssim(a, b, data_range=255))

def diff_map(a, b):
    if a.ndim == 3 and b.ndim == 3:
        d = cv2.absdiff(a, b)
        return d, cv2.cvtColor(d, cv2.COLOR_BGR2GRAY)
    else:
        d = cv2.absdiff(a, b)
        return d, d

def heatmap_from_gray(gray):
    norm = cv2.normalize(gray, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
    return cv2.applyColorMap(norm, cv2.COLORMAP_JET)


# ================== Comparación ==================

def compare_images(original_path, master_path, save_dir, mode="letterbox", show=False):
    save_dir = Path(save_dir); save_dir.mkdir(parents=True, exist_ok=True)
    orig_raw = robust_imread_any(original_path)
    mast_raw = robust_imread_any(master_path)
    if orig_raw is None or mast_raw is None:
        raise RuntimeError("No se pudieron leer imágenes")

    orig_bgr, mast_bgr = to_bgr(orig_raw), to_bgr(mast_raw)

    mask_master, mask_info = compute_mask_from_master(mast_raw)

    th, tw = orig_bgr.shape[:2]
    if mode == "fill":
        mast_fit, _ = fill_resize(mast_bgr, (th, tw), interpolation=cv2.INTER_CUBIC)
        mask_fit = cv2.resize(mask_master, (tw, th), interpolation=cv2.INTER_NEAREST) if mask_master is not None else None
    else:
        mast_fit, (scale,left,top) = letterbox_strict_keep_aspect(mast_bgr, (th, tw))
        mask_fit, _ = letterbox_mask(mask_master, (th, tw)) if mask_master is not None else (None, None)

    orig_gray, mast_gray = cv2.cvtColor(orig_bgr, cv2.COLOR_BGR2GRAY), cv2.cvtColor(mast_fit, cv2.COLOR_BGR2GRAY)
    _mse, _psnr, _ssim = mse(orig_gray, mast_gray, mask_fit), psnr(orig_gray, mast_gray, mask_fit), compute_ssim(orig_bgr, mast_fit, mask_fit)

    diff_color, diff_gray = diff_map(orig_bgr, mast_fit)
    diff_gray_masked = diff_gray.copy()

    roi_stats, heat_roi = None, None
    if mask_fit is not None:
        valid = mask_fit > 0
        if valid.sum() > 0.002*valid.size:
            diff_gray_masked[~valid] = 0
            ys,xs = np.where(valid)
            y0,y1,x0,x1 = ys.min(), ys.max()+1, xs.min(), xs.max()+1
            diff_gray_roi = cv2.absdiff(orig_gray[y0:y1,x0:x1], mast_gray[y0:y1,x0:x1])
            diff_gray_roi[mask_fit[y0:y1,x0:x1]==0] = 0
            heat_roi = heatmap_from_gray(diff_gray_roi)
            roi_stats = {
                "bbox": (x0,y0,x1-x0,y1-y0),
                "min": int(diff_gray_roi[mask_fit[y0:y1,x0:x1]>0].min()),
                "max": int(diff_gray_roi[mask_fit[y0:y1,x0:x1]>0].max()),
                "mean": float(diff_gray_roi[mask_fit[y0:y1,x0:x1]>0].mean())
            }

    heat = heatmap_from_gray(diff_gray_masked)

    # Guardar
    cv2.imwrite(str(save_dir/"01_original_bgr.png"), orig_bgr)
    cv2.imwrite(str(save_dir/"02_master_aligned_bgr.png"), mast_fit)
    if mask_fit is not None: cv2.imwrite(str(save_dir/"03_mask_used.png"), mask_fit)
    cv2.imwrite(str(save_dir/"04_diff_gray.png"), diff_gray_masked)
    cv2.imwrite(str(save_dir/"05_diff_heatmap.png"), heat)
    if heat_roi is not None:
        cv2.imwrite(str(save_dir/"06_diff_gray_roi.png"), diff_gray_roi)
        cv2.imwrite(str(save_dir/"07_diff_heatmap_roi.png"), heat_roi)

    if show:
        cv2.imshow("Diff Heatmap", heat)
        if heat_roi is not None: cv2.imshow("Diff Heatmap ROI", heat_roi)
        cv2.waitKey(0); cv2.destroyAllWindows()

    # Informe
    lines = []
    lines.append(f"Original: {Path(original_path).name}")
    lines.append(f"Master  : {Path(master_path).name}")
    lines.append(f"Máscara: {mask_info}")
    lines.append(f"MSE={_mse:.4f}  PSNR={_psnr:.3f} dB  SSIM={_ssim if _ssim is not None else 'N/D'}")
    if roi_stats: lines.append(f"ROI bbox={roi_stats['bbox']} diff[min={roi_stats['min']}, max={roi_stats['max']}, mean={roi_stats['mean']:.2f}]")
    lines.append(f"Guardado en: {save_dir.resolve()}")
    return "\n".join(lines)


# ================== GUI ==================

class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("Comparador Original vs Master")
        self.geometry("700x450")

        self.var_original, self.var_master = tk.StringVar(), tk.StringVar()
        self.var_outdir = tk.StringVar(value=str((Path.cwd()/"cmp_out").resolve()))
        self.var_mode = tk.StringVar(value="letterbox")
        self.var_show = tk.BooleanVar(value=False)

        frm = ttk.Frame(self); frm.pack(fill="both", expand=True, padx=10, pady=6)

        ttk.Label(frm, text="Imagen ORIGINAL:").grid(row=0, column=0, sticky="w")
        ttk.Entry(frm, textvariable=self.var_original, width=64).grid(row=0, column=1)
        ttk.Button(frm, text="Examinar...", command=self.browse_original).grid(row=0, column=2)

        ttk.Label(frm, text="Imagen MASTER:").grid(row=1, column=0, sticky="w")
        ttk.Entry(frm, textvariable=self.var_master, width=64).grid(row=1, column=1)
        ttk.Button(frm, text="Examinar...", command=self.browse_master).grid(row=1, column=2)

        ttk.Label(frm, text="Carpeta salida:").grid(row=2, column=0, sticky="w")
        ttk.Entry(frm, textvariable=self.var_outdir, width=64).grid(row=2, column=1)
        ttk.Button(frm, text="Examinar...", command=self.browse_outdir).grid(row=2, column=2)

        ttk.Label(frm, text="Modo de encaje:").grid(row=3, column=0, sticky="w")
        ttk.Radiobutton(frm, text="LETTERBOX", variable=self.var_mode, value="letterbox").grid(row=3, column=1, sticky="w")
        ttk.Radiobutton(frm, text="FILL", variable=self.var_mode, value="fill").grid(row=3, column=1, sticky="e")

        ttk.Checkbutton(frm, text="Mostrar ventanas", variable=self.var_show).grid(row=4, column=1, sticky="w")
        ttk.Button(frm, text="Comparar", command=self.run_compare).grid(row=5, column=1, pady=10)

        ttk.Label(frm, text="Resultados:").grid(row=6, column=0, sticky="nw")
        self.txt = tk.Text(frm, height=12, width=80, wrap="word")
        self.txt.grid(row=6, column=1, columnspan=2)

    def browse_original(self):
        f = filedialog.askopenfilename(filetypes=[("Imágenes", "*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff"),("Todos","*.*")])
        if f: self.var_original.set(f)

    def browse_master(self):
        f = filedialog.askopenfilename(filetypes=[("Imágenes", "*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff"),("Todos","*.*")])
        if f: self.var_master.set(f)

    def browse_outdir(self):
        d = filedialog.askdirectory()
        if d: self.var_outdir.set(d)

    def run_compare(self):
        try:
            text = compare_images(self.var_original.get(), self.var_master.get(),
                                  self.var_outdir.get(), mode=self.var_mode.get(), show=self.var_show.get())
            self.txt.delete("1.0", "end"); self.txt.insert("1.0", text)
            messagebox.showinfo("OK", "Comparación terminada. Resultados guardados.")
        except Exception as e:
            messagebox.showerror("Error", str(e))


if __name__ == "__main__":
    app = App(); app.mainloop()
