"""RTMDet-only inpainting TCP server.

该版本专注于从 Unity 接收帧后，完全依赖 RTMDet 实例分割来生成手部掩码，
并通过 OpenCV 做修补，再把处理结果回传给 Unity。不会使用 Unity 上传的
骨架掩码。
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
        raise ValueError("--inference-width 和 --inference-height 需要成对提供")

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

            # 即使客户端还在上传掩码，这里也会读掉但完全忽略。
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
                f"[frame] size={img_length:6d} bytes | mask={mask_length:6d} (ignored) | "
                f"infer={infer_ms:6.1f} ms | total={total_ms:6.1f} ms | fps={fps:5.1f}",
            )
    except ConnectionError as exc:
        print(f"[-] {addr} disconnected: {exc}")
    finally:
        conn.close()


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="RTMDet-only inpainting TCP server")
    parser.add_argument("--host", default="127.0.0.1", help="监听地址")
    parser.add_argument("--port", type=int, default=5555, help="监听端口")
    parser.add_argument(
        "--config",
        default=str(PC_INPAINT / "config" / "rtmdet-ins_s.py"),
        help="模型配置文件路径",
    )
    parser.add_argument(
        "--weights",
        default=str(PC_INPAINT / "weights" / "rtmdet-ins_s.pth"),
        help="模型权重路径",
    )
    parser.add_argument("--device", default="cuda:0", help="推理设备，例如 cuda:0 或 cpu")
    parser.add_argument(
        "--target-label",
        dest="target_label",
        action="append",
        help="需要移除的目标类别，可重复指定",
    )
    parser.add_argument(
        "--score-threshold",
        type=float,
        default=0.3,
        help="置信度阈值，低于该值的检测会被忽略",
    )
    parser.add_argument("--inference-width", type=int, help="推理前的缩放宽度")
    parser.add_argument("--inference-height", type=int, help="推理前的缩放高度")
    parser.add_argument("--inpaint-radius", type=int, default=3, help="OpenCV inpaint 半径")
    parser.add_argument("--jpeg-quality", type=int, default=80, help="回传 JPEG 质量")
    parser.add_argument("--warmup", action="store_true", help="启动时先跑一次空推理，减少首帧延迟")
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
