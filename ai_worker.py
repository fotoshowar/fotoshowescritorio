"""
ai_worker.py — Servidor TCP local de reconocimiento facial
=========================================================
Puerto: 54321 (localhost only)
Protocolo: JSON lines — una petición por línea, una respuesta por línea

Peticiones soportadas:
  {"cmd":"ping"}
  {"cmd":"analyze", "path":"C:/fotos/img001.jpg"}
  {"cmd":"analyze_batch", "paths":["img1.jpg","img2.jpg",...]}
  {"cmd":"quit"}

Respuestas:
  {"status":"ok", "pong":true}
  {"status":"ok", "path":"...", "faces":2, "embedding":[0.12,...]}
  {"status":"error", "message":"..."}

Instalación:
  pip install insightface onnxruntime opencv-python pillow numpy
"""

import sys
import json
import socket
import threading
import traceback
import os
import gc
import numpy as np

HOST = "127.0.0.1"
PORT = 54321
MODEL_NAME = "buffalo_sc"   # modelo ligero — buen balance velocidad/precisión
                             # alternativa: "buffalo_l" para máxima precisión

# ── Carga del modelo ─────────────────────────────────────────
face_app = None
model_lock = threading.Lock()

def load_model():
    global face_app
    try:
        import insightface
        from insightface.app import FaceAnalysis
        print(json.dumps({"status": "loading", "message": "Cargando InsightFace..."}), flush=True)
        app = FaceAnalysis(
            name=MODEL_NAME,
            providers=["CPUExecutionProvider"]
        )
        app.prepare(ctx_id=-1, det_size=(640, 640))
        face_app = app
        print(json.dumps({"status": "ready", "message": "Modelo listo"}), flush=True)
    except Exception as e:
        print(json.dumps({"status": "error", "message": f"Error cargando modelo: {e}"}), flush=True)
        sys.exit(1)


# ── Análisis de una foto ──────────────────────────────────────
def analyze_photo(path: str) -> dict:
    if not os.path.exists(path):
        return {"status": "error", "path": path, "message": "Archivo no encontrado"}

    try:
        from PIL import Image, ImageOps
        import cv2

        # Cargar con EXIF orientation
        pil = ImageOps.exif_transpose(Image.open(path)).convert("RGB")
        img = cv2.cvtColor(np.array(pil), cv2.COLOR_RGB2BGR)

        with model_lock:
            faces = face_app.get(cv2.cvtColor(img, cv2.COLOR_BGR2RGB))

        faces_count = len(faces)
        embedding = None

        if faces:
            # Usar la cara más grande (la principal)
            best = max(faces, key=lambda f:
                (f.bbox[2] - f.bbox[0]) * (f.bbox[3] - f.bbox[1]))
            emb = best.embedding
            # Normalizar a vector unitario
            norm = np.linalg.norm(emb)
            if norm > 0:
                embedding = (emb / norm).tolist()

        del img; gc.collect()

        return {
            "status": "ok",
            "path": path,
            "faces": faces_count,
            "embedding": embedding
        }

    except Exception as e:
        return {
            "status": "error",
            "path": path,
            "message": str(e),
            "traceback": traceback.format_exc()
        }


# ── Handler de cliente TCP ────────────────────────────────────
def handle_client(conn, addr):
    try:
        buf = b""
        with conn:
            while True:
                chunk = conn.recv(65536)
                if not chunk:
                    break
                buf += chunk

                # Procesar todas las líneas completas recibidas
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    line = line.strip()
                    if not line:
                        continue

                    try:
                        req = json.loads(line.decode("utf-8"))
                    except json.JSONDecodeError as e:
                        resp = {"status": "error", "message": f"JSON inválido: {e}"}
                        conn.sendall((json.dumps(resp) + "\n").encode("utf-8"))
                        continue

                    cmd = req.get("cmd", "")

                    if cmd == "ping":
                        resp = {"status": "ok", "pong": True}

                    elif cmd == "analyze":
                        path = req.get("path", "")
                        resp = analyze_photo(path)

                    elif cmd == "analyze_batch":
                        paths = req.get("paths", [])
                        results = []
                        for p in paths:
                            results.append(analyze_photo(p))
                        resp = {
                            "status": "ok",
                            "results": results,
                            "total": len(results)
                        }

                    elif cmd == "quit":
                        resp = {"status": "ok", "message": "Cerrando..."}
                        conn.sendall((json.dumps(resp) + "\n").encode("utf-8"))
                        return

                    else:
                        resp = {"status": "error", "message": f"Comando desconocido: {cmd}"}

                    conn.sendall((json.dumps(resp) + "\n").encode("utf-8"))

    except Exception as e:
        print(json.dumps({"status": "error", "message": f"Error en cliente: {e}"}), flush=True)


# ── Servidor TCP ──────────────────────────────────────────────
def run_server():
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)

    try:
        server.bind((HOST, PORT))
    except OSError as e:
        print(json.dumps({"status": "error", "message": f"Puerto {PORT} ocupado: {e}"}), flush=True)
        sys.exit(1)

    server.listen(10)
    print(json.dumps({
        "status": "listening",
        "host": HOST,
        "port": PORT,
        "message": f"Escuchando en {HOST}:{PORT}"
    }), flush=True)

    try:
        while True:
            conn, addr = server.accept()
            t = threading.Thread(
                target=handle_client,
                args=(conn, addr),
                daemon=True
            )
            t.start()
    except KeyboardInterrupt:
        pass
    finally:
        server.close()


if __name__ == "__main__":
    load_model()
    run_server()
