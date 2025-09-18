import os
from PIL import Image
import numpy as np

# Cambia esto a tu ruta real
folder = r"data/validation/defective"
# Puedes filtrar solo los nombres base (sin extensión)
bases = set(os.path.splitext(f)[0] for f in os.listdir(folder))
formats = ["BMP", "jpeg", "png", "jpg"]  # por si acaso tienes .jpg

for base in sorted(bases):
    found = []
    for ext in formats:
        fname = f"{base}.{ext}"
        path = os.path.join(folder, fname)
        if os.path.exists(path):
            img = Image.open(path).convert("RGB")
            arr = np.array(img)
            found.append((ext, arr.shape, arr[0,0,:]))
    if found:
        print(f"== {base} ==")
        for ext, shape, px in found:
            print(f"  {ext.upper()}: shape={shape} primer píxel RGB={px}")
        print()
