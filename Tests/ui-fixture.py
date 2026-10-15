"""Serve the isolated UI fixture with the host SwarmUI's Bootstrap, dropdown handlers, and theme assets.

All API calls in the fixture are mocked; this never reads or writes user settings.
"""
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote, urlsplit
import argparse

ROOT = Path(__file__).resolve().parent.parent
SWARM = ROOT.parent.parent / 'wwwroot'


class Handler(SimpleHTTPRequestHandler):
    def do_GET(self):
        if self.path == '/':
            self.path = '/Tests/ui-fixture.html'
        super().do_GET()

    def translate_path(self, path):
        path = unquote(urlsplit(path).path)
        if path.startswith('/swarm/'):
            base, relative = SWARM, path[7:]
        elif path.startswith('/fonts/'):
            base, relative = SWARM, path.lstrip('/')
        else:
            base, relative = ROOT, path.lstrip('/')
        candidate = (base / relative).resolve()
        return str(candidate if candidate.is_relative_to(base) else base / '__not_found__')

    def guess_type(self, path):
        mime = super().guess_type(path)
        if mime.startswith('text/') or mime in ('application/javascript',):
            mime += '; charset=utf-8'
        return mime

    def end_headers(self):
        self.send_header('Cache-Control', 'no-store')
        super().end_headers()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8767)
    args = parser.parse_args()
    print(f'MagicPrompt UI fixture: http://127.0.0.1:{args.port}', flush=True)
    ThreadingHTTPServer(('127.0.0.1', args.port), Handler).serve_forever()
