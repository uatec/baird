# Static server for the prototype: no caching (so a reload always gets the latest build), threaded.
import http.server, sys
class NoCache(http.server.SimpleHTTPRequestHandler):
    def end_headers(self):
        self.send_header('Cache-Control', 'no-store')
        super().end_headers()
    def log_message(self, *a):
        pass
host, port = sys.argv[1], int(sys.argv[2])
http.server.ThreadingHTTPServer((host, port), NoCache).serve_forever()
