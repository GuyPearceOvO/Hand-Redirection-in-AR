"""RTMDet inpainter with morphology + temporal smoothing (does not rely on prior mask).

This module provides RTMDetInpainterStable, a drop-in alternative to
PC_Inpaint/rtmdet_inpainter.RTMDetInpainter. It keeps inference identical but
adds:
- morphology cleanup (close + dilate)
- small-component removal (min_area)
- short temporal persistence (keep_frames) to reduce per-frame misses

Usage: import RTMDetInpainterStable and call .inpaint(image_bgr).
"""
from __future__ import annotations

from typing import Iterable, Optional, Sequence, Tuple

import cv2
import numpy as np

# Reuse the original implementation for inferencer and mask assembly
from rtmdet_inpainter import RTMDetInpainter as _Base  # type: ignore


class RTMDetInpainterStable(_Base):
    def __init__(
        self,
        *,
        config_path: str,
        weights_path: str,
        device: str = "cuda:0",
        target_labels: Optional[Iterable[str]] = None,
        score_threshold: float = 0.3,
        inference_size: Optional[Tuple[int, int]] = None,
        inpaint_radius: int = 3,
        inpaint_flags: int = cv2.INPAINT_TELEA,
        warmup: bool = False,
        # new options
        mask_dilate: int = 2,
        mask_close: int = 3,
        min_area: int = 64,
        keep_frames: int = 2,
        roi_margin: int = 20,
    ) -> None:
        super().__init__(
            config_path=config_path,
            weights_path=weights_path,
            device=device,
            target_labels=target_labels,
            score_threshold=score_threshold,
            inference_size=inference_size,
            inpaint_radius=inpaint_radius,
            inpaint_flags=inpaint_flags,
            warmup=warmup,
        )
        self.mask_dilate = int(mask_dilate)
        self.mask_close = int(mask_close)
        self.min_area = int(min_area)
        self.keep_frames = int(keep_frames)
        self.roi_margin = max(0, int(roi_margin))
        self._prev_mask: Optional[np.ndarray] = None
        self._prev_ttl: int = 0
        self.last_debug: Optional[dict[str, np.ndarray]] = None

    def inpaint(self, image_bgr: np.ndarray, prior_mask: Optional[np.ndarray] = None) -> np.ndarray:  # type: ignore[override]
        if image_bgr is None or image_bgr.ndim != 3 or image_bgr.shape[2] != 3:
            raise ValueError("image_bgr must be HxWx3 BGR")

        h, w = image_bgr.shape[:2]
        # Choose working resolution
        if self.inference_size and (w, h) != self.inference_size:
            infer_w, infer_h = self.inference_size
            working = cv2.resize(image_bgr, (infer_w, infer_h), interpolation=cv2.INTER_LINEAR)
        else:
            working = image_bgr
            infer_w, infer_h = w, h

        # Inference in RGB
        result = self._run_inference(cv2.cvtColor(working, cv2.COLOR_BGR2RGB))
        det_mask = np.zeros((infer_h, infer_w), dtype=bool)
        if result and "predictions" in result and result["predictions"]:
            preds = result["predictions"][0]
            det_mask = self._build_combined_mask(preds, (infer_h, infer_w))

        # Resize back to full frame
        if (infer_h, infer_w) != (h, w):
            det_mask = cv2.resize(det_mask.astype(np.uint8), (w, h), interpolation=cv2.INTER_NEAREST).astype(bool)

        mask = det_mask

        # Temporal persistence
        if not mask.any() and self._prev_mask is not None and self._prev_ttl > 0:
            mask = self._prev_mask.copy()
            self._prev_ttl -= 1
        elif mask.any():
            self._prev_mask = mask.copy()
            self._prev_ttl = max(0, self.keep_frames)

        # Remove tiny blobs
        if self.min_area > 1:
            mask = self._filter_small_components(mask, self.min_area)

        # Morphology: close holes then dilate edges
        k = max(self.mask_close, self.mask_dilate)
        if k > 0:
            kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k * 2 + 1, k * 2 + 1))
            mu8 = (mask.astype(np.uint8)) * 255
            if self.mask_close > 0:
                mu8 = cv2.morphologyEx(mu8, cv2.MORPH_CLOSE, kernel, iterations=1)
            if self.mask_dilate > 0:
                mu8 = cv2.dilate(mu8, kernel, iterations=1)
            mask = mu8 > 0

        inpaint_mask = (mask.astype(np.uint8)) * 255
        if not mask.any():
            repaired = image_bgr.copy()
            bbox = np.array([0, 0, 0, 0], dtype=np.int32)
        else:
            ys, xs = np.nonzero(mask)
            margin = self.roi_margin
            y0 = max(int(ys.min()) - margin, 0)
            y1 = min(int(ys.max()) + margin, h - 1)
            x0 = max(int(xs.min()) - margin, 0)
            x1 = min(int(xs.max()) + margin, w - 1)
            if y1 <= y0 or x1 <= x0:
                repaired = cv2.inpaint(image_bgr, inpaint_mask, self.inpaint_radius, self.inpaint_flags)
                bbox = np.array([0, h - 1, 0, w - 1], dtype=np.int32)
            else:
                output = image_bgr.copy()
                roi_img = output[y0 : y1 + 1, x0 : x1 + 1]
                roi_mask = inpaint_mask[y0 : y1 + 1, x0 : x1 + 1]
                roi_result = cv2.inpaint(roi_img, roi_mask, self.inpaint_radius, self.inpaint_flags)
                output[y0 : y1 + 1, x0 : x1 + 1] = roi_result
                repaired = output
                bbox = np.array([y0, y1, x0, x1], dtype=np.int32)

        self.last_debug = {
            "det_mask": det_mask.astype(np.uint8),
            "final_mask": mask.astype(np.uint8),
            "inpaint_mask": inpaint_mask,
            "bbox": bbox,
        }

        return repaired

    @staticmethod
    def _filter_small_components(mask: np.ndarray, min_area: int) -> np.ndarray:
        if min_area <= 1:
            return mask
        mu8 = (mask.astype(np.uint8) if mask.dtype != np.uint8 else mask)
        if mu8.ndim != 2 or mu8.size == 0:
            return mask
        num, labels, stats, _ = cv2.connectedComponentsWithStats((mu8 > 0).astype(np.uint8), connectivity=8)
        if num <= 1:
            return mu8 > 0
        keep = np.zeros_like(mu8, dtype=bool)
        for i in range(1, num):
            if stats[i, cv2.CC_STAT_AREA] >= min_area:
                keep |= (labels == i)
        return keep
