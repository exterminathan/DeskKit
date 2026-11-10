"""Fake GitHub Releases feed for the DeskKit update tests (contract 16.10). Local only, never the internet.

    python fake_feed.py --root <dir> --port-file <file>

<dir>/feed.json is read on every request, so a test can change the scenario between runs:

    {"repos": {"exterminathan/DeskKit": [ <release>, ... ],
               "exterminathan/region-control": {"status": 500}}}

A <release> is GitHub-shaped: tag_name, draft, prerelease, html_url, assets[] with name,
browser_download_url and (optional) digest. "{base}" in any string becomes http://127.0.0.1:<port>.
Assets are served from <dir>/assets/<name>; a missing file gives 404 (download failure).
GET /repos/<owner>/<repo>/releases answers with an ETag and honours If-None-Match (304).
Every request is appended to <dir>/requests.log as "<status> <path>".
"""
import argparse
import hashlib
import http.server
import json
import os
import socketserver
import sys
import urllib.parse


def make_handler(root):
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, fmt, *args):  # keep the console quiet
            pass

        def _log(self, status):
            with open(os.path.join(root, "requests.log"), "a", encoding="utf-8") as f:
                f.write(f"{status} {self.path}\n")

        def _send(self, status, body=b"", ctype="application/json", headers=None):
            self.send_response(status)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            for k, v in (headers or {}).items():
                self.send_header(k, v)
            self.end_headers()
            if body:
                self.wfile.write(body)
            self._log(status)

        def do_GET(self):
            url = urllib.parse.urlparse(self.path)
            parts = [p for p in url.path.split("/") if p]
            base = f"http://127.0.0.1:{self.server.server_address[1]}"
            if len(parts) == 4 and parts[0] == "repos" and parts[3] == "releases":
                try:
                    with open(os.path.join(root, "feed.json"), encoding="utf-8-sig") as f:
                        feed = json.load(f)
                except (OSError, ValueError) as e:
                    return self._send(500, str(e).encode(), "text/plain")
                repo = f"{parts[1]}/{parts[2]}"
                rel = feed.get("repos", {}).get(repo)
                if rel is None:
                    return self._send(404, b'{"message":"Not Found"}')
                if isinstance(rel, dict):
                    return self._send(int(rel.get("status", 500)), json.dumps(rel).encode())
                body = json.dumps(rel).replace("{base}", base).encode("utf-8")
                etag = '"' + hashlib.sha1(body).hexdigest() + '"'
                if self.headers.get("If-None-Match") == etag:
                    return self._send(304, headers={"ETag": etag})
                return self._send(200, body, headers={"ETag": etag})
            if len(parts) == 2 and parts[0] == "assets":
                name = os.path.basename(urllib.parse.unquote(parts[1]))
                path = os.path.join(root, "assets", name)
                if not os.path.isfile(path):
                    return self._send(404, b"missing", "text/plain")
                with open(path, "rb") as f:
                    data = f.read()
                return self._send(200, data, "application/octet-stream")
            return self._send(404, b"unknown path", "text/plain")

    return Handler


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True)
    ap.add_argument("--port-file", required=True)
    a = ap.parse_args()
    root = os.path.abspath(a.root)
    socketserver.TCPServer.allow_reuse_address = False
    with socketserver.ThreadingTCPServer(("127.0.0.1", 0), make_handler(root)) as srv:
        srv.daemon_threads = True
        tmp = a.port_file + ".tmp"
        with open(tmp, "w", encoding="ascii") as f:
            f.write(str(srv.server_address[1]))
        os.replace(tmp, a.port_file)
        try:
            srv.serve_forever()
        except KeyboardInterrupt:
            pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
