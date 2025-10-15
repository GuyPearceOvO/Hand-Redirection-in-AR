import socket
import struct
import threading
from typing import Optional

import cv2
import numpy as np

HOST = "0.0.0.0"
PORT = 5555
JPEG_QUALITY = 80


def recv_exact(sock: socket.socket, size: int) -> bytes:
    data = bytearray()
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise ConnectionError("remote closed")
        data.extend(chunk)
    return bytes(data)


def decode_image(data: bytes) -> Optional[np.ndarray]:
    if not data:
        return None
    arr = np.frombuffer(data, dtype=np.uint8)
    img = cv2.imdecode(arr, cv2.IMREAD_COLOR)
    return img


def encode_image(image: np.ndarray) -> Optional[bytes]:
    success, buffer = cv2.imencode(
        ".jpg",
        image,
        [int(cv2.IMWRITE_JPEG_QUALITY), JPEG_QUALITY],
    )
    if not success:
        return None
    return buffer.tobytes()


def add_overlay(image: np.ndarray) -> np.ndarray:
    overlay = image.copy()
    h, w = overlay.shape[:2]
    color = (0, 0, 255)
    thickness = max(6, min(h, w) // 50)
    cv2.rectangle(overlay, (20, 20), (w - 20, h - 20), color, thickness)
    cv2.putText(
        overlay,
        "RTM Inpaint",
        (int(w * 0.2), int(h * 0.9)),
        cv2.FONT_HERSHEY_SIMPLEX,
        2.0,
        color,
        thickness,
        cv2.LINE_AA,
    )
    return overlay


def send_frame(conn: socket.socket, payload: bytes) -> None:
    conn.sendall(struct.pack("!I", len(payload)))
    conn.sendall(payload)


def handle_client(conn: socket.socket, addr):
    print(f"[+] connected from {addr}")
    try:
        while True:
            header = recv_exact(conn, 4)
            (length,) = struct.unpack("!I", header)
            if length <= 0:
                print(f"[frame] invalid length {length}, closing {addr}")
                break

            payload = recv_exact(conn, length)
            print(f"[frame] bytes={len(payload)}")

            image = decode_image(payload)
            if image is None:
                print(f"[warn] decode failed, echoing raw payload to {addr}")
                send_frame(conn, payload)
                continue

            print(f"[frame] shape={image.shape}")
            processed = add_overlay(image)
            encoded = encode_image(processed)
            if encoded is None:
                print(f"[warn] encode failed, echoing raw payload to {addr}")
                send_frame(conn, payload)
                continue

            send_frame(conn, encoded)
    except Exception as exc:
        print(f"[-] {addr} disconnected: {exc}")
    finally:
        conn.close()


def main():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        server.bind((HOST, PORT))
        server.listen()
        print(f"[*] listening on {HOST}:{PORT}")
        while True:
            client, addr = server.accept()
            threading.Thread(target=handle_client, args=(client, addr), daemon=True).start()


if __name__ == "__main__":
    main()