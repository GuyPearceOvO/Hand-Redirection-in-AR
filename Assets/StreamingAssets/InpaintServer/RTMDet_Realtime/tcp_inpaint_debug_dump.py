"""RTMDet inpaint TCP server with debug dumping of original image and masks.

This is a drop-in replacement for tcp_inpaint_server_skeleton.py adding options:
  --debug-dir   directory to save {orig, prior, det, union, inpaint, overlays}
  --debug-every dump one frame every N frames per connection
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

from rtmdet_inpainter import RTMDetInpainter  # type: ignore


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


def decode_mask(data: bytes, shape: Tuple[int, int]) -> Optional[np.ndarray]:
    if not data:
        return None
    expected = shape[0] * shape[1]
    if len(data) != expected:
        return None
    arr = np.frombuffer(data, dtype=np.uint8)
    return arr.reshape(shape)


def overlay_mask(image_bgr: np.ndarray, mask_u8: np.ndarray, color=(0, 0, 255), alpha: float = 0.4) -> np.ndarray:
    if image_bgr is None or mask_u8 is None:
        return image_bgr
    mask_bool = mask_u8.astype(bool)
    img = image_bgr if image_bgr.ndim == 3 and image_bgr.shape[2] == 3 else cv2.cvtColor(image_bgr, cv2.COLOR_GRAY2BGR)
    overlay = img.copy()
    color_arr = np.zeros_like(img)
    color_arr[:, :] = color
    overlay[mask_bool] = cv2.addWeighted(img[mask_bool], 1.0 - alpha, color_arr[mask_bool], alpha, 0)
    return overlay


def save_debug_frame(
    out_dir: Path,
    index: int,
    orig_bgr: np.ndarray,
    processed_bgr: Optional[np.ndarray],
    prior_mask: Optional[np.ndarray],
    det_mask: Optional[np.ndarray],
    union_mask: Optional[np.ndarray],
) -> None:
    try:
        out_dir.mkdir(parents=True, exist_ok=True)
        stem = f"{index:06d}"
        cv2.imwrite(str(out_dir / f"{stem}_orig.jpg"), orig_bgr)
        if processed_bgr is not None:
            cv2.imwrite(str(out_dir / f"{stem}_inpaint.jpg"), processed_bgr)

        def _save_mask(mask: Optional[np.ndarray], name: str, color=(0, 0, 255)) -> None:
            if mask is None:
                return
            mu8 = (mask.astype(np.uint8) if mask.dtype != np.uint8 else mask)
            if mu8.ndim == 3:
                mu8 = cv2.cvtColor(mu8, cv2.COLOR_BGR2GRAY)
            mu8 = (mu8 > 0).astype(np.uint8) * 255
            cv2.imwrite(str(out_dir / f"{stem}_{name}.png"), mu8)
            overlay = overlay_mask(orig_bgr, mu8, color=color, alpha=0.4)
            cv2.imwrite(str(out_dir / f"{stem}_{name}_overlay.jpg"), overlay)

        _save_mask(prior_mask, "prior", color=(0, 255, 0))
        _save_mask(det_mask, "det", color=(0, 0, 255))
        _save_mask(union_mask, "union", color=(255, 0, 0))
    except Exception as ex:
        print(f"[debug] save_debug_frame failed: {ex}")


def build_inpainter(args: argparse.Namespace) -> RTMDetInpainter:
    inference_size: Optional[Tuple[int, int]] = None
    if args.inference_width and args.inference_height:
        inference_size = (args.inference_width, args.inference_height)
    elif args.inference_width or args.inference_height:
        raise ValueError("--inference-width and --inference-height must be provided together")

    target_labels = args.target_label or ()
    if not target_labels:
        target_labels = ("person",)

    return RTMDetInpainter(
        config_path=args.config,
        weights_path=args.weights,
        device=args.device,
        target_labels=target_labels,
        score_threshold=args.score_threshold,
        inference_size=inference_size,
        inpaint_radius=args.inpaint_radius,
        inpaint_flags=cv2.INPAINT_TELEA,
        warmup=args.warmup,
    )


def handle_client(
    conn: socket.socket,
    addr: Tuple[str, int],
    *,
    inpainter: RTMDetInpainter,
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
            mask_payload = recv_exact(conn, mask_length) if mask_length > 0 else b""
            recv_time = time.perf_counter()
            image = decode_image(payload)
            if image is None:
                print(f"[warn] decode failed, echoing raw payload to {addr}")
                send_frame(conn, payload)
                continue

            prior_mask = None
            if mask_payload:
                prior_mask = decode_mask(mask_payload, image.shape[:2])
                if prior_mask is not None:
                    prior_mask = cv2.GaussianBlur(prior_mask, (15, 15), 0)
                    _, prior_mask = cv2.threshold(prior_mask, 32, 255, cv2.THRESH_BINARY)

            try:
                t0 = time.perf_counter()
                with infer_lock:
                    processed = inpainter.inpaint(image, prior_mask=prior_mask)
                infer_ms = (time.perf_counter() - t0) * 1000.0
            except Exception as exc:  # pragma: no cover
                print(f"[error] inference failed: {exc}")
                processed = image
                infer_ms = -1.0

            # Optional debug dump every N frames
            if debug_dir is not None and debug_every and (frame_index % max(1, debug_every) == 0):
                try:
                    # Build detection mask at inference size then resize back
                    infer_w, infer_h = image.shape[1], image.shape[0]
                    working = image
                    if getattr(inpainter, 'inference_size', None):
                        _w, _h = inpainter.inference_size
                        working = cv2.resize(image, (_w, _h), interpolation=cv2.INTER_LINEAR)
                        infer_w, infer_h = _w, _h
                    rgb_image = cv2.cvtColor(working, cv2.COLOR_BGR2RGB)
                    result = inpainter._run_inference(rgb_image)
                    det_mask = None
                    if result and 'predictions' in result and result['predictions']:
                        preds = result['predictions'][0]
                        det_mask = inpainter._build_combined_mask(preds, (infer_h, infer_w))
                        if (infer_h, infer_w) != image.shape[:2]:
                            det_mask = cv2.resize(det_mask.astype(np.uint8), (image.shape[1], image.shape[0]), interpolation=cv2.INTER_NEAREST).astype(bool)
                    if det_mask is None:
                        det_mask = np.zeros(image.shape[:2], dtype=bool)

                    union_mask = det_mask.copy()
                    if prior_mask is not None:
                        union_mask = np.logical_or(union_mask, prior_mask > 0)

                    save_debug_frame(debug_dir, frame_index, image, processed, prior_mask, det_mask, union_mask)
                except Exception as dbg_ex:
                    print(f"[debug] dump failed: {dbg_ex}")

            encoded = encode_image(processed, jpeg_quality)
            if encoded is None:
                print(f"[warn] encode failed, echoing raw payload to {addr}")
                send_frame(conn, payload)
                continue

            send_frame(conn, encoded)
            total_ms = (time.perf_counter() - recv_time) * 1000.0
            fps = 1000.0 / total_ms if total_ms > 0 else 0.0
            print(
                f"[frame] size={img_length:6d} bytes | mask={mask_length:6d} | infer={infer_ms:6.1f} ms | "
                f"total={total_ms:6.1f} ms | fps={fps:5.1f}",
            )
            frame_index += 1
    except ConnectionError as exc:
        print(f"[-] {addr} disconnected: {exc}")
    finally:
        conn.close()


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="RTMDet inpainting TCP server (debug dump)")
    parser.add_argument("--host", default="127.0.0.1", help="Address to bind")
    parser.add_argument("--port", type=int, default=5566, help="Port to bind")
    parser.add_argument(
        "--config",
        default=str(PC_INPAINT / "config" / "rtmdet-ins_s.py"),
        help="Model config path",
    )
    parser.add_argument(
        "--weights",
        default=str(PC_INPAINT / "weights" / "rtmdet-ins_s.pth"),
        help="Model weights path",
    )
    parser.add_argument("--device", default="cuda:0", help="Device for inference, e.g. cuda:0 or cpu")
    parser.add_argument("--target-label", dest="target_label", action="append", help="Label(s) to inpaint; repeat for multiple")
    parser.add_argument("--score-threshold", type=float, default=0.3, help="Ignore detections below this score")
    parser.add_argument("--inference-width", type=int, help="Optional resize width before inference")
    parser.add_argument("--inference-height", type=int, help="Optional resize height before inference")
    parser.add_argument("--inpaint-radius", type=int, default=3, help="OpenCV inpaint radius")
    parser.add_argument("--jpeg-quality", type=int, default=80, help="JPEG quality for response")
    parser.add_argument("--warmup", action="store_true", help="Run one warmup inference during startup")
    parser.add_argument("--debug-dir", type=str, default="", help="Directory to dump debug frames/masks")
    parser.add_argument("--debug-every", type=int, default=30, help="Dump one frame every N frames")
    return parser.parse_args(argv)


def main(argv: Optional[Sequence[str]] = None) -> None:
    args = parse_args(argv)
    inpainter = build_inpainter(args)
    infer_lock = threading.Lock()

    debug_dir = Path(args.debug_dir) if args.debug_dir else None

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
                    "debug_dir": debug_dir,
                    "debug_every": max(1, int(args.debug_every)),
                },
                daemon=True,
            )
            thread.start()


if __name__ == "__main__":
    main()
