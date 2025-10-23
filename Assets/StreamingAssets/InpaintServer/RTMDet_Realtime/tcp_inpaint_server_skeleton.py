"""Ultraleap → RTMDet inpaint TCP server (local PC pipeline).

This version runs on the PC, receives JPEG frames from Unity, removes hands using
RTMDet instance segmentation + OpenCV inpainting, and sends the repaired frame
back over the same TCP connection.
"""
from __future__ import annotations

import argparse
import socket
import struct
import sys
import threading
import time
from pathlib import Path
from typing import Optional, Sequence, Tuple

import cv2
import numpy as np

SERVER_ROOT = Path(__file__).resolve().parents[1]
PC_INPAINT = SERVER_ROOT / "PC_Inpaint"
if str(PC_INPAINT) not in sys.path:
    sys.path.insert(0, str(PC_INPAINT))

from rtmdet_inpainter import RTMDetInpainter  # type: ignore  # noqa: E402


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
    success, buffer = cv2.imencode(
        ".jpg",
        image,
        [int(cv2.IMWRITE_JPEG_QUALITY), int(quality)],
    )
    if not success:
        return None
    return buffer.tobytes()


def send_frame(conn: socket.socket, payload: bytes) -> None:
    conn.sendall(struct.pack("!I", len(payload)))
    conn.sendall(payload)


def build_inpainter(args: argparse.Namespace) -> RTMDetInpainter:
    inference_size: Optional[Tuple[int, int]] = None
    if args.inference_width and args.inference_height:
        inference_size = (args.inference_width, args.inference_height)
    elif args.inference_width or args.inference_height:
        raise ValueError("--inference-width and --inference-height must be provided together")

    target_labels = tuple(args.target_label) if args.target_label else ("person",)

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


def decode_mask(data: bytes, shape: Tuple[int, int]) -> Optional[np.ndarray]:
    if not data:
        return None
    expected = shape[0] * shape[1]
    if len(data) != expected:
        return None
    arr = np.frombuffer(data, dtype=np.uint8)
    return arr.reshape(shape)


def handle_client(
    conn: socket.socket,
    addr: Tuple[str, int],
    *,
    inpainter: RTMDetInpainter,
    infer_lock: threading.Lock,
    jpeg_quality: int,
) -> None:
    print(f"[+] connected from {addr}")
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
    except ConnectionError as exc:
        print(f"[-] {addr} disconnected: {exc}")
    finally:
        conn.close()


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="RTMDet inpainting TCP server")
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
    parser.add_argument(
        "--target-label",
        dest="target_label",
        action="append",
        default=["person"],
        help="Label(s) to inpaint; repeat for multiple (default: person)",
    )
    parser.add_argument(
        "--score-threshold",
        type=float,
        default=0.3,
        help="Ignore detections below this score",
    )
    parser.add_argument("--inference-width", type=int, help="Optional resize width before inference")
    parser.add_argument("--inference-height", type=int, help="Optional resize height before inference")
    parser.add_argument("--inpaint-radius", type=int, default=3, help="OpenCV inpaint radius")
    parser.add_argument("--jpeg-quality", type=int, default=80, help="JPEG quality for response")
    parser.add_argument("--warmup", action="store_true", help="Run one warmup inference during startup")
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
                },
                daemon=True,
            )
            thread.start()


if __name__ == "__main__":
    main()
