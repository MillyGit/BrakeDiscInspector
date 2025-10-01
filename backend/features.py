# Añade estos imports si no están ya presentes en features.py
import inspect
import torch
import torch.nn.functional as F

# --- Pega estos métodos dentro de tu clase (p.ej. DinoV2Features) ---

def _prepare_input_size(self, model, x: torch.Tensor):
    """
    Asegura que el tamaño (H,W) de x cuadra con el modelo ViT.
    - Si existe patch_embed.set_input_size y (H,W) son múltiplos del patch, lo usa (sin reescalar).
    - Si no, reescala a model.patch_embed.img_size (p.ej. 518x518).
    Devuelve (x_preparado, modo) con modo in {"set_input_size","resize","unchanged"}.
    """
    pe = getattr(model, "patch_embed", None)
    if pe is None:
        return x, "unchanged"

    H, W = x.shape[-2:]
    ps = getattr(pe, "patch_size", (getattr(self, "patch", 14), getattr(self, "patch", 14)))
    if isinstance(ps, int):
        ps = (ps, ps)

    img_size = getattr(pe, "img_size", (H, W))
    if isinstance(img_size, int):
        img_size = (img_size, img_size)
    target_h, target_w = int(img_size[0]), int(img_size[1])

    if (H, W) == (target_h, target_w):
        return x, "unchanged"

    if hasattr(pe, "set_input_size") and (H % ps[0] == 0) and (W % ps[1] == 0):
        pe.set_input_size((H, W))
        # Algunas implementaciones también guardan img_size en el modelo
        if hasattr(model, "img_size"):
            model.img_size = (H, W)
        return x, "set_input_size"

    x_res = F.interpolate(x, size=(target_h, target_w), mode="bilinear", align_corners=False)
    return x_res, "resize"


def _forward_tokens(self, x: torch.Tensor) -> torch.Tensor:
    """
    Versión robusta basada en tu método original:
    - Soporta firmas cambiantes de get_intermediate_layers (return_class_token / return_cls_token / reshape).
    - Ajusta el tamaño de entrada (set_input_size o resize) para evitar 'Input height ... doesn't match model ...'.
    - Mantiene tu lógica de out_indices, reshape=True, sin CLS y concatenación de canales.
    """
    # --- Preparar tamaño: evita la aserción de timm en patch_embed ---
    x, _how_size = self._prepare_input_size(self.model, x)

    def _call_get_intermediate_layers_compat(model, x, out_indices, want_cls: bool, want_reshape: bool):
        fn = getattr(model, "get_intermediate_layers", None)
        if fn is None:
            return None

        sig = inspect.signature(fn)
        kwargs = {}
        if "return_class_token" in sig.parameters:
            kwargs["return_class_token"] = want_cls
        elif "return_cls_token" in sig.parameters:
            kwargs["return_cls_token"] = want_cls
        if "reshape" in sig.parameters:
            kwargs["reshape"] = want_reshape

        # 1) Lista de índices (tu caso original)
        try:
            return fn(x, out_indices, **kwargs)
        except TypeError:
            pass
        # 2) Entero n = len(out_indices)
        try:
            n = len(out_indices) if out_indices else 1
            return fn(x, n, **kwargs)
        except TypeError:
            pass
        # 3) Sin kwargs
        try:
            return fn(x, out_indices)
        except TypeError:
            n = len(out_indices) if out_indices else 1
            return fn(x, n)

    def _normalize_layers(layers):
        feats = []
        for layer in layers:
            # timm puede devolver (tokens, cls, ...) -> nos quedamos con tokens
            if isinstance(layer, (tuple, list)) and len(layer) > 0 and torch.is_tensor(layer[0]):
                layer = layer[0]
            if not torch.is_tensor(layer):
                raise RuntimeError(f"Tipo inesperado en capa intermedia: {type(layer)}")

            if layer.ndim == 4:
                # (B, H, W, C) -> (B, HW, C)
                b, h, w, c = layer.shape
                feats.append(layer.reshape(b, h * w, c))
            elif layer.ndim == 3:
                feats.append(layer)  # (B, HW, C)
            else:
                raise RuntimeError(f"Forma inesperada en capa intermedia: {layer.shape}")
        return feats

    if self.out_indices and hasattr(self.model, "get_intermediate_layers"):
        try:
            layers = _call_get_intermediate_layers_compat(
                self.model,
                x,
                self.out_indices,
                want_cls=False,
                want_reshape=True,
            )
            if layers is not None:
                feats = _normalize_layers(layers)
                tokens = torch.cat(feats, dim=-1)
                return tokens[0]
        except Exception:
            # Si falla por alguna firma rara, cae al camino forward_features
            pass

    # --- Camino clásico: forward_features (tu lógica original) ---
    feats = self.model.forward_features(x)
    if isinstance(feats, dict):
        t = feats.get("x") or feats.get("tokens")
        if t is None:
            t = max((v for v in feats.values() if isinstance(v, torch.Tensor)), key=lambda u: u.numel())
    else:
        t = feats

    if t.ndim == 3:
        if t.shape[1] == self._expected_tokens(t.shape[1]):
            tokens = t
        else:
            tokens = t[:, 1:, :]
    elif t.ndim == 4:
        b, c, h, w = t.shape
        tokens = t.permute(0, 2, 3, 1).reshape(b, h * w, c)
    else:
        raise RuntimeError(f"Forma inesperada de features: {t.shape}")

    return tokens[0]
