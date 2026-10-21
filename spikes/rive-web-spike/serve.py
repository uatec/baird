#!/usr/bin/env python3
"""
Simple HTTP server for Baird Rive Web Spike.
Serves static files with correct MIME types for .wasm and .riv assets,
and launches the default browser.
"""

import sys
import os
import argparse
import socket
import webbrowser
from http.server import HTTPServer, SimpleHTTPRequestHandler

class RiveRequestHandler(SimpleHTTPRequestHandler):
    extensions_map = {
        **SimpleHTTPRequestHandler.extensions_map,
        '.wasm': 'application/wasm',
        '.riv': 'application/octet-stream',
        '.js': 'application/javascript',
        '.json': 'application/json',
        '.html': 'text/html; charset=utf-8',
        '.css': 'text/css; charset=utf-8',
    }

    def end_headers(self):
        # Enable CORS and caching headers for local development
        self.send_header('Access-Control-Allow-Origin', '*')
        self.send_header('Cache-Control', 'no-cache, no-store, must-revalidate')
        super().end_headers()

    def log_message(self, format, *args):
        # Concise logging
        sys.stderr.write(f"[RiveSpikeServer] {self.address_string()} - {format % args}\n")

def find_available_port(start_port=8088, max_attempts=50):
    for port in range(start_port, start_port + max_attempts):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            try:
                s.bind(('127.0.0.1', port))
                return port
            except OSError:
                continue
    return start_port

def main():
    parser = argparse.ArgumentParser(description="Serve Baird Rive Web Spike")
    parser.add_argument("--port", "-p", type=int, default=0, help="Port to listen on (0 for auto)")
    parser.add_argument("--no-browser", action="store_true", help="Do not open default web browser")
    args = parser.parse_args()

    # Change directory to this script's directory
    script_dir = os.path.dirname(os.path.abspath(__file__))
    os.chdir(script_dir)

    port = args.port if args.port > 0 else find_available_port(8088)
    server_address = ('127.0.0.1', port)

    httpd = HTTPServer(server_address, RiveRequestHandler)
    url = f"http://127.0.0.1:{port}/"
    print(f"\n=================================================")
    print(f"  Baird Spike 3: Rive Web View Spike Server")
    print(f"  URL: {url}")
    print(f"  Directory: {script_dir}")
    print(f"  Press Ctrl+C to stop.")
    print(f"=================================================\n")

    if not args.no_browser:
        try:
            webbrowser.open(url)
        except Exception as e:
            print(f"Could not open browser: {e}")

    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nStopping server...")
        httpd.server_close()

if __name__ == "__main__":
    main()
