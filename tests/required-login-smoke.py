"""Exercise the web authentication boundary against an isolated fake backend."""
import base64
import contextlib
import http.client
import http.cookies
import http.server
import os
import socket
import subprocess
import tempfile
import threading
import time
import urllib.parse
from html.parser import HTMLParser

class Backend(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        expected = "Basic " + base64.b64encode(b"testuser:test-password").decode()
        if self.path == "/hosting/session":
            self.send_response(200 if self.headers.get("Authorization") == expected else 401)
        else:
            self.send_response(404)
        self.end_headers()
    def log_message(self, *args):
        pass

class FormParser(HTMLParser):
    def __init__(self, html):
        super().__init__()
        self.fields = {}
        self.feed(html)
    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "input" and attrs.get("type") == "hidden":
            self.fields[attrs.get("name")] = attrs.get("value", "")

def main():
    backend = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Backend)
    threading.Thread(target=backend.serve_forever, daemon=True).start()
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    env = dict(os.environ)
    env.pop("ASPNETCORE_HTTPS_PORT", None)
    env.update(ASPNETCORE_ENVIRONMENT="Development", ASPNETCORE_URLS=f"http://127.0.0.1:{port}",
               HostingPanel__Backend="Kubernetes",
               HostingPanel__Bootstrap__Username="testuser",
               HostingPanel__Bootstrap__Password="test-password",
               ConnectionStrings__HostingPanel=f"Data Source={os.path.join(tempfile.gettempdir(), f'hostingpanel-login-{os.getpid()}.db')}")
    cookies = {}
    def request(method, path, form=None, update=True):
        headers = {"Cookie": "; ".join(f"{key}={value}" for key, value in cookies.items())}
        body = None
        if form is not None:
            headers["Content-Type"] = "application/x-www-form-urlencoded"
            body = urllib.parse.urlencode(form)
        conn = http.client.HTTPConnection("127.0.0.1", port, timeout=15)
        try:
            conn.request(method, path, body, headers)
            response = conn.getresponse()
            html = response.read().decode(errors="replace")
            if update:
                for value in response.headers.get_all("Set-Cookie", []):
                    jar = http.cookies.SimpleCookie()
                    jar.load(value)
                    for name, morsel in jar.items():
                        if morsel["max-age"] == "0" or not morsel.value:
                            cookies.pop(name, None)
                        else:
                            cookies[name] = morsel.value
            return response.status, response.headers, html
        finally:
            conn.close()

    with tempfile.TemporaryFile() as log:
        proc = subprocess.Popen(["dotnet", "run", "--no-build", "-c", "Release",
                                 "--no-launch-profile", "--project", "src/HostingPanel.Web"],
                                env=env, stdout=log, stderr=subprocess.STDOUT)
        try:
            for _ in range(60):
                try:
                    status, _, _ = request("GET", "/login")
                    if status == 200:
                        break
                except OSError:
                    pass
                if proc.poll() is not None:
                    raise RuntimeError("Web process exited")
                time.sleep(1)
            else:
                raise RuntimeError("Web process did not become ready")

            for path in ["/", "/users", "/domains", "/backend-settings", "/tickets/new"]:
                status, headers, html = request("GET", path)
                assert status == 302, (path, status)
                location = urllib.parse.urlsplit(headers["Location"])
                assert location.path == "/login"
                assert urllib.parse.parse_qs(location.query)["ReturnUrl"] == [path]
                assert 'mud-layout' not in html

            status, _, html = request("GET", "/login?ReturnUrl=%2Fusers")
            assert status == 200 and 'action="/login"' in html and 'mud-layout' not in html
            fields = FormParser(html).fields
            assert fields.get("__RequestVerificationToken")
            assert request("POST", "/login", {"username": "testuser", "password": "test-password"})[0] == 400
            assert request("POST", "/login", dict(fields, username="testuser", password="wrong"))[0] == 401
            status, headers, _ = request("POST", "/login",
                dict(fields, username="testuser", password="test-password", remember="true"))
            assert status == 302 and headers["Location"] == "/users"
            session_header = next(h for h in headers.get_all("Set-Cookie", []) if h.startswith("__Host-HostingPanel="))
            assert "secure" in session_header.lower() and "httponly" in session_header.lower()
            assert "expires=" in session_header.lower()
            saved_cookie = cookies["__Host-HostingPanel"]

            # Explicit cookie forwarding on loopback HTTP tests the server session;
            # deployed browsers only transmit this secure cookie over HTTPS.
            status, _, html = request("GET", "/")
            assert status == 200 and 'mud-layout' in html and "testuser" in html
            status, _, html = request("GET", "/")
            assert status == 200 and 'mud-layout' in html
            logout = FormParser(html).fields
            assert logout.get("__RequestVerificationToken")
            assert request("POST", "/logout", {})[0] == 400
            status, headers, _ = request("POST", "/logout", logout)
            assert status == 302 and headers["Location"] == "/login"
            cookies["__Host-HostingPanel"] = saved_cookie
            assert request("GET", "/users")[0] == 302
            print("Mandatory login, persistent cookie, session restoration, CSRF and logout: OK")
        except BaseException:
            log.seek(0)
            print(log.read().decode(errors="replace"))
            raise
        finally:
            proc.terminate()
            with contextlib.suppress(subprocess.TimeoutExpired):
                proc.wait(timeout=10)
            if proc.poll() is None:
                proc.kill()
                proc.wait()
            backend.shutdown()

if __name__ == "__main__":
    main()
