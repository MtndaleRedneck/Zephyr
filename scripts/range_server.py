"""Directory HTTP server with Range support for the VLC playback proof.
The phone's StreamServer serves ranges; VLC seeks through them.

Optional slow mode (CP1 round 2, the slow-phone spinner leg):
  range_server.py DIR PORT [SLOW_SECONDS CHUNK_BYTES]
For SLOW_SECONDS after the first request, every response is written CHUNK_BYTES
per 100 ms (a slow but live phone); after that, full speed."""
import http.server, sys, os, re, time, threading

DIR, PORT = sys.argv[1], int(sys.argv[2])
SLOW = float(sys.argv[3]) if len(sys.argv) > 3 else 0.0
CHUNK = int(sys.argv[4]) if len(sys.argv) > 4 else 1024
_first = [None]
_lock = threading.Lock()

def slow_phase():
    with _lock:
        if _first[0] is None:
            _first[0] = time.monotonic()
        return time.monotonic() - _first[0] < SLOW

class H(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=DIR, **kw)
    def log_message(self, *a):
        pass
    def write_body(self, data):
        if SLOW <= 0:
            self.wfile.write(data)
            return
        pos = 0
        while pos < len(data):
            if slow_phase():
                self.wfile.write(data[pos:pos + CHUNK]); self.wfile.flush()
                pos += CHUNK
                time.sleep(0.1)
            else:
                self.wfile.write(data[pos:]); pos = len(data)
    def do_GET(self):
        path = self.translate_path(self.path)
        if not os.path.isfile(path) or (not self.headers.get("Range") and SLOW <= 0):
            return super().do_GET()
        size = os.path.getsize(path)
        start, end = 0, size - 1
        status = 200
        if self.headers.get("Range"):
            m = re.match(r"bytes=(\d+)-(\d*)", self.headers["Range"])
            start = int(m.group(1)); end = int(m.group(2)) if m.group(2) else size - 1
            end = min(end, size - 1)
            status = 206
        with open(path, "rb") as f:
            f.seek(start); data = f.read(end - start + 1)
        self.send_response(status)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Accept-Ranges", "bytes")
        if status == 206:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        try:
            self.write_body(data)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass

server_class = http.server.ThreadingHTTPServer if SLOW > 0 else http.server.HTTPServer
server_class(("127.0.0.1", PORT), H).serve_forever()
