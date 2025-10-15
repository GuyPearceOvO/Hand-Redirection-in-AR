"""RTMDet inference + OpenCV inpainting helper module.

This module wraps RTMDet instance segmentation together with OpenCV inpainting so
other scripts (e.g. the TCP server) can reuse the logic without duplicating code.
"""
from __future__ import annotations

import threading
from typing import Iterable, Optional, Sequence, Tuple

import cv2
import numpy as np
from mmdet.apis import DetInferencer
from pycocotools import mask as mask_utils


def _normalize_labels(labels: Optional[Iterable[str]]) -> Sequence[str]:
    if not labels:
        return tuple()
    return tuple(label.strip() for label in labels if label and label.strip())


class RTMDetInpainter:
    """Wrap RTMDet inference and OpenCV inpainting into a reusable helper."""

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
    ) -> None:
        self.inference_size = inference_size
        self.score_threshold = float(score_threshold)
        self.target_labels = set(_normalize_labels(target_labels))
        self.inpaint_radius = int(inpaint_radius)
        self.inpaint_flags = int(inpaint_flags)
        self._lock = threading.Lock()

        self.inferencer = DetInferencer(
            model=config_path,
            weights=weights_path,
            device=device,
        )

        meta = getattr(self.inferencer.model, "dataset_meta", {}) or {}
        self.class_names = tuple(meta.get("classes", ()))

        if warmup:
            self.warmup()

    def warmup(self, width: int = 320, height: int = 240) -> None:
        """Run one dry pass to hide the first-call latency spike."""
        dummy_w = width
        dummy_h = height
        if self.inference_size:
            dummy_w, dummy_h = self.inference_size
        dummy = np.zeros((dummy_h, dummy_w, 3), dtype=np.uint8)
        _ = self._run_inference(cv2.cvtColor(dummy, cv2.COLOR_BGR2RGB))

    def inpaint(self, image_bgr: np.ndarray, prior_mask: Optional[np.ndarray] = None) -> np.ndarray:
        """Execute segmentation + inpainting on a BGR input image."""
        if image_bgr is None:
            raise ValueError("image_bgr must not be None")
        if image_bgr.ndim != 3 or image_bgr.shape[2] != 3:
            raise ValueError("image_bgr must have shape HxWx3")

        original_h, original_w = image_bgr.shape[:2]
        crop_bounds = None
        working = image_bgr
        prior_roi_mask = None

        if prior_mask is not None:
            prior_mask = np.asarray(prior_mask, dtype=np.uint8)
            if prior_mask.shape[:2] != (original_h, original_w):
                prior_mask = cv2.resize(prior_mask, (original_w, original_h), interpolation=cv2.INTER_NEAREST)
            if np.count_nonzero(prior_mask) > 0:
                ys, xs = np.nonzero(prior_mask)
                margin = max(min(original_w, original_h) // 20, 16)
                y0 = max(int(ys.min()) - margin, 0)
                y1 = min(int(ys.max()) + margin, original_h - 1)
                x0 = max(int(xs.min()) - margin, 0)
                x1 = min(int(xs.max()) + margin, original_w - 1)
                if y1 - y0 > 2 and x1 - x0 > 2:
                    crop_bounds = (y0, y1 + 1, x0, x1 + 1)
                    working = image_bgr[y0:y1 + 1, x0:x1 + 1].copy()
                    prior_roi_mask = prior_mask[y0:y1 + 1, x0:x1 + 1]
            else:
                prior_mask = None

        if self.inference_size and (original_w, original_h) != self.inference_size:
            infer_w, infer_h = self.inference_size
            working_resized = cv2.resize(working, (infer_w, infer_h), interpolation=cv2.INTER_LINEAR)
        else:
            working_resized = working
            infer_w, infer_h = working.shape[1], working.shape[0]

        rgb_image = cv2.cvtColor(working_resized, cv2.COLOR_BGR2RGB)
        result = self._run_inference(rgb_image)

        if not result or "predictions" not in result or not result["predictions"]:
            if prior_mask is not None:
                return cv2.inpaint(image_bgr, prior_mask, self.inpaint_radius, self.inpaint_flags)
            return image_bgr.copy()

        preds = result["predictions"][0]
        target_shape = (infer_h, infer_w)
        combined_mask = self._build_combined_mask(preds, target_shape)

        if not combined_mask.any():
            if prior_mask is not None:
                return cv2.inpaint(image_bgr, prior_mask, self.inpaint_radius, self.inpaint_flags)
            return image_bgr.copy()

        if (infer_h, infer_w) != working.shape[:2]:
            combined_mask = cv2.resize(
                combined_mask.astype(np.uint8),
                (working.shape[1], working.shape[0]),
                interpolation=cv2.INTER_NEAREST,
            ).astype(bool)

        if prior_roi_mask is not None:
            union_mask = np.logical_or(combined_mask, prior_roi_mask > 0)
        elif prior_mask is not None:
            full_prior = prior_mask.astype(bool)
            if crop_bounds is not None:
                y0, y1, x0, x1 = crop_bounds
                union_mask = np.zeros((original_h, original_w), dtype=bool)
                union_mask[y0:y1, x0:x1] = combined_mask
                union_mask |= full_prior
            else:
                union_mask = full_prior
        else:
            union_mask = combined_mask

        if crop_bounds is not None and union_mask.shape != (original_h, original_w):
            y0, y1, x0, x1 = crop_bounds
            full_mask = np.zeros((original_h, original_w), dtype=bool)
            full_mask[y0:y1, x0:x1] = union_mask
            union_mask = full_mask

        if prior_mask is not None and union_mask.shape != prior_mask.shape:
            union_mask = cv2.resize(
                union_mask.astype(np.uint8),
                (prior_mask.shape[1], prior_mask.shape[0]),
                interpolation=cv2.INTER_NEAREST,
            ).astype(bool)

        inpaint_mask = (union_mask.astype(np.uint8)) * 255
        repaired = cv2.inpaint(image_bgr, inpaint_mask, self.inpaint_radius, self.inpaint_flags)

        return repaired

    def _run_inference(self, image_rgb: np.ndarray) -> dict:
        with self._lock:
            return self.inferencer(
                inputs=image_rgb,
                show=False,
                no_save_pred=True,
                no_save_vis=True,
                out_dir=None,
            )

    def _build_combined_mask(self, preds: dict, target_shape: Tuple[int, int]) -> np.ndarray:
        masks = preds.get("masks") or []
        labels = preds.get("labels") or []
        scores = preds.get("scores")

        combined = np.zeros(target_shape, dtype=bool)

        if not len(masks):
            return combined

        for idx, label_id in enumerate(labels):
            label_name = self._label_name(int(label_id))
            if self.target_labels and label_name not in self.target_labels:
                continue

            if scores is not None and idx < len(scores) and scores[idx] < self.score_threshold:
                continue

            mask = self._decode_mask(masks[idx])
            if mask.size == 0:
                continue

            if mask.shape != target_shape:
                mask = cv2.resize(mask.astype(np.uint8), (target_shape[1], target_shape[0]), interpolation=cv2.INTER_NEAREST).astype(bool)

            combined |= mask

        return combined

    def _label_name(self, label_id: int) -> str:
        if 0 <= label_id < len(self.class_names):
            return self.class_names[label_id]
        return str(label_id)

    @staticmethod
    def _decode_mask(mask_obj) -> np.ndarray:
        if mask_obj is None:
            return np.zeros((0, 0), dtype=bool)

        if isinstance(mask_obj, dict) and "size" in mask_obj and "counts" in mask_obj:
            mask = mask_utils.decode(mask_obj)
        else:
            mask = np.asarray(mask_obj)

        if mask.ndim == 3:
            mask = mask[..., 0]

        if mask.size == 0:
            return np.zeros((0, 0), dtype=bool)

        return mask.astype(bool)
