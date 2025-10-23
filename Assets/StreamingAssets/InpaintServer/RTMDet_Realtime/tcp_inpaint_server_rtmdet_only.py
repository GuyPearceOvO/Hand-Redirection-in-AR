"""RTMDet-only TCP server (ignores prior mask), using morphology + temporal smoothing.

Receives JPEG frames from Unity, runs RTMDet instance segmentation and OpenCV
inpainting using RTMDetInpainterStable, and returns the repaired frame.
"""
from __future__ import annotations

import argparse
import socket
import struct
import threading
import time
from pathlib import Path
from typing import Optional, Sequence, Tuple

import cv2
import numpy as np

SERVER_ROOT = Path(__file__).resolve().parents[1]
PC_INPAINT = SERVER_ROOT / "PC_Inpaint"
import sys
if str(PC_INPAINT) not in sys.path:
    sys.path.insert(0, str(PC_INPAINT))

from rtmdet_inpainter_stable import RTMDetInpainterStable  # type: ignore


def overlay_mask(image_bgr: np.ndarray, mask_u8: np.ndarray, color=(0, 0, 255), alpha: float = 0.4) -> np.ndarray:
    if image_bgr is None or mask_u8 is None:
        return image_bgr
    img = image_bgr if image_bgr.ndim == 3 and image_bgr.shape[2] == 3 else cv2.cvtColor(image_bgr, cv2.COLOR_GRAY2BGR)
    mask_bool = mask_u8.astype(bool)
    if not mask_bool.any():
        return img
    overlay = img.copy()
    color_arr = np.zeros_like(img)
    color_arr[:, :] = color
    overlay[mask_bool] = cv2.addWeighted(img[mask_bool], 1.0 - alpha, color_arr[mask_bool], alpha, 0)
    return overlay


def save_debug_frame(out_dir: Path, index: int, orig_bgr: np.ndarray, processed_bgr: np.ndarray, debug_info: dict) -> None:
    try:
        out_dir.mkdir(parents=True, exist_ok=True)
        stem = f"{index:06d}"
        cv2.imwrite(str(out_dir / f"{stem}_orig.jpg"), orig_bgr)
        cv2.imwrite(str(out_dir / f"{stem}_inpaint.jpg"), processed_bgr)

        det_mask = debug_info.get("det_mask")
        final_mask = debug_info.get("final_mask")

        if det_mask is not None:
            det_u8 = (det_mask.astype(np.uint8) * 255) if det_mask.max() <= 1 else det_mask.astype(np.uint8)
            cv2.imwrite(str(out_dir / f"{stem}_det.png"), det_u8)
            cv2.imwrite(str(out_dir / f"{stem}_det_overlay.jpg"), overlay_mask(orig_bgr, det_u8, color=(0, 0, 255)))

        if final_mask is not None:
            union_u8 = (final_mask.astype(np.uint8) * 255) if final_mask.max() <= 1 else final_mask.astype(np.uint8)
            cv2.imwrite(str(out_dir / f"{stem}_union.png"), union_u8)
            cv2.imwrite(str(out_dir / f"{stem}_union_overlay.jpg"), overlay_mask(orig_bgr, union_u8, color=(255, 0, 0)))
    except Exception as exc:  # pragma: no cover
        print(f"[debug] failed to save frame {index}: {exc}")


def recv_exact(sock: socket.socket, size: int) -> bytes:
    data = bytearray()
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise ConnectionError("remote closed the connection")
        data.extend(chunk)
    return bytes(data)


def decode_image(data: bytes) -> Optional[np.ndarray]:
    if not data:
        return None
    arr = np.frombuffer(data, dtype=np.uint8)
    return cv2.imdecode(arr, cv2.IMREAD_COLOR)


def encode_image(image: np.ndarray, quality: int) -> Optional[bytes]:
    ok, buf = cv2.imencode(".jpg", image, [int(cv2.IMWRITE_JPEG_QUALITY), int(quality)])
    if not ok:
        return None
    return buf.tobytes()


def send_frame(conn: socket.socket, payload: bytes) -> None:
    conn.sendall(struct.pack("!I", len(payload)))
    conn.sendall(payload)


def build_inpainter(args: argparse.Namespace) -> RTMDetInpainterStable:
    inference_size: Optional[Tuple[int, int]] = None
    if args.inference_width and args.inference_height:
        inference_size = (args.inference_width, args.inference_height)
    elif args.inference_width or args.inference_height:
        raise ValueError("--inference-width and --inference-height must be provided together")

    target_labels = args.target_label or ()
    if not target_labels:
        target_labels = ("person",)

    return RTMDetInpainterStable(
        config_path=args.config,
        weights_path=args.weights,
        device=args.device,
        target_labels=target_labels,
        score_threshold=args.score_threshold,
        inference_size=inference_size,
        inpaint_radius=args.inpaint_radius,
        inpaint_flags=cv2.INPAINT_TELEA,
        warmup=args.warmup,
        mask_dilate=args.mask_dilate,
        mask_close=args.mask_close,
        min_area=args.min_area,
        keep_frames=args.keep_frames,
        roi_margin=args.roi_margin,
    )


def handle_client(
    conn: socket.socket,
    addr: Tuple[str, int],
    *,
    inpainter: RTMDetInpainterStable,
    infer_lock: threading.Lock,
    jpeg_quality: int,
    debug_dir: Optional[Path] = None,
    debug_every: int = 0,
) -> None:
    print(f"[+] connected from {addr}")
    frame_index = 0
    try:
        while True:
            header = recv_exact(conn, 8)
            img_length, mask_length = struct.unpack("!II", header)
            if img_length <= 0:
                print(f"[warn] invalid frame length {img_length}, closing {addr}")
                break

            payload = recv_exact(conn, img_length)
            # Discard any mask payload (server is RTMDet-only)
            if mask_length > 0:
                _ = recv_exact(conn, mask_length)

            recv_time = time.perf_counter()
            image = decode_image(payload)
            if image is None:
                print(f"[warn] decode failed, echoing raw payload to {addr}")
                send_frame(conn, payload)
                continue

            try:
                t0 = time.perf_counter()
                with infer_lock:
                    processed = inpainter.inpaint(image, prior_mask=None)
                infer_ms = (time.perf_counter() - t0) * 1000.0
                debug_info = getattr(inpainter, "last_debug", None)
            except Exception as exc:  # pragma: no cover
                print(f"[error] inference failed: {exc}")
                processed = image
                infer_ms = -1.0
                debug_info = None

            if (
                debug_dir is not None
                and debug_every > 0
                and frame_index % debug_every == 0
                and debug_info
            ):
                save_debug_frame(debug_dir, frame_index, image, processed, debug_info)

            encoded = encode_image(processed, jpeg_quality)
            if encoded is None:
                print(f"[warn] encode failed, echoing raw payload to {addr}")
                send_frame(conn, payload)
                continue

            send_frame(conn, encoded)
            total_ms = (time.perf_counter() - recv_time) * 1000.0
            fps = 1000.0 / total_ms if total_ms > 0 else 0.0
            print(
                f"[frame] size={img_length:6d} bytes | infer={infer_ms:6.1f} ms | total={total_ms:6.1f} ms | fps={fps:5.1f}",
            )
            frame_index += 1
    except ConnectionError as exc:
        print(f"[-] {addr} disconnected: {exc}")
    finally:
        conn.close()


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="RTMDet-only inpainting TCP server")
    parser.add_argument("--host", default="127.0.0.1", help="Address to bind")
    parser.add_argument("--port", type=int, default=5566, help="Port to bind")
    parser.add_argument("--config", default=str(PC_INPAINT / "config" / "rtmdet-ins_s.py"), help="Model config path")
    parser.add_argument("--weights", default=str(PC_INPAINT / "weights" / "rtmdet-ins_s.pth"), help="Model weights path")
    parser.add_argument("--device", default="cuda:0", help="Device for inference, e.g. cuda:0 or cpu")
    parser.add_argument("--target-label", dest="target_label", action="append", help="Label(s) to inpaint; repeat for multiple")
    parser.add_argument("--score-threshold", type=float, default=0.3, help="Ignore detections below this score")
    parser.add_argument("--inference-width", type=int, help="Optional resize width before inference")
    parser.add_argument("--inference-height", type=int, help="Optional resize height before inference")
    parser.add_argument("--inpaint-radius", type=int, default=3, help="OpenCV inpaint radius")
    parser.add_argument("--jpeg-quality", type=int, default=80, help="JPEG quality for response")
    parser.add_argument("--warmup", action="store_true", help="Run one warmup inference during startup")
    # post-processing controls
    parser.add_argument("--mask-dilate", type=int, default=2, help="Dilate mask by k pixels (approx, via morphology)")
    parser.add_argument("--mask-close", type=int, default=3, help="Close small holes (approx radius in pixels)")
    parser.add_argument("--min-area", type=int, default=64, help="Filter blobs smaller than this many pixels")
    parser.add_argument("--keep-frames", type=int, default=2, help="Temporal persistence when a frame briefly misses")
    parser.add_argument("--roi-margin", type=int, default=20, help="Margin (pixels) around detected bbox for ROI inpaint")
    parser.add_argument("--debug-dir", type=str, default="", help="Optional directory to dump debug frames/masks")
    parser.add_argument("--debug-every", type=int, default=0, help="Dump one frame every N frames (0=off)")
    return parser.parse_args(argv)


def main(argv: Optional[Sequence[str]] = None) -> None:
    args = parse_args(argv)
    inpainter = build_inpainter(args)
    infer_lock = threading.Lock()

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        server.bind((args.host, args.port))
        server.listen()
        print(f"[*] listening on {args.host}:{args.port}")
        while True:
            conn, addr = server.accept()
            thread = threading.Thread(
                target=handle_client,
                args=(conn, addr),
                kwargs={
                    "inpainter": inpainter,
                    "infer_lock": infer_lock,
                    "jpeg_quality": args.jpeg_quality,
                    "debug_dir": Path(args.debug_dir) if args.debug_dir else None,
                    "debug_every": max(0, args.debug_every),
                },
                daemon=True,
            )
            thread.start()


if __name__ == "__main__":
    main()
