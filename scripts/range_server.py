"""Directory HTTP server with Range support for the VLC playback proof.
The phone's StreamServer serves ranges; VLC seeks through them."""
import http.server, sys, os, re, functools

DIR, PORT = sys.argv[1], int(sys.argv[2])

class H(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=DIR, **kw)
    def do_GET(self):
        path = self.translate_path(self.path)
        if not os.path.isfile(path) or not self.headers.get("Range"):
            return super().do_GET()
        size = os.path.getsize(path)
        m = re.match(r"bytes=(\d+)-(\d*)", self.headers["Range"])
        start = int(m.group(1)); end = int(m.group(2)) if m.group(2) else size - 1
        end = min(end, size - 1)
        with open(path, "rb") as f:
            f.seek(start); data = f.read(end - start + 1)
        self.send_response(206)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

http.server.HTTPServer(("127.0.0.1", PORT), H).serve_forever()
