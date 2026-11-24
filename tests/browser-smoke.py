"""Exercise interactive MudBlazor navigation in an isolated local instance."""
import os
import socket
import subprocess
import tempfile
import time
import urllib.request
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

def main():
    with tempfile.TemporaryDirectory() as directory:
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            port = sock.getsockname()[1]
        base = f"http://localhost:{port}"
        env = dict(os.environ)
        env.pop("ASPNETCORE_HTTPS_PORT", None)
        env.update(
            ASPNETCORE_ENVIRONMENT="Development",
            ASPNETCORE_URLS=base,
            HostingPanel__Backend="Kubernetes",
            HostingPanel__Bootstrap__Username="testuser",
            HostingPanel__Bootstrap__Password="ci-browser-test-password",
            ConnectionStrings__HostingPanel=f"Data Source={Path(directory) / 'browser.db'}",
        )
        with tempfile.TemporaryFile() as log:
            proc = subprocess.Popen(
                ["dotnet", "run", "--no-build", "-c", "Release", "--no-launch-profile",
                 "--project", "src/HostingPanel.Web"],
                env=env, stdout=log, stderr=subprocess.STDOUT,
                start_new_session=True,
            )
            try:
                for _ in range(60):
                    if proc.poll() is not None:
                        raise RuntimeError("Web process exited")
                    try:
                        with urllib.request.urlopen(base + "/login", timeout=2) as response:
                            if response.status == 200:
                                break
                    except OSError:
                        time.sleep(1)
                else:
                    raise RuntimeError("Web process did not become ready")
                with sync_playwright() as pw:
                    browser = pw.chromium.launch()
                    context = browser.new_context(viewport={"width": 1440, "height": 900}, locale="en-US")
                    page = context.new_page()
                    errors = []
                    page.on("pageerror", lambda error: errors.append(str(error)))
                    page.goto(base + "/")
                    expect(page.locator('form[action="/login"]')).to_be_visible()
                    page.locator('input[name="username"]').fill("testuser")
                    page.locator('input[name="password"]').fill("ci-browser-test-password")
                    page.locator('form[action="/login"] button[type="submit"]').click()
                    expect(page.locator(".mud-layout")).to_be_visible(timeout=20000)
                    expect(page.get_by_role("heading", name="Control panel", exact=True)).to_be_visible()
                    # A real component event, not merely server-rendered HTML.
                    user = page.get_by_role("button", name="User", exact=True)
                    expect(user).to_be_visible()
                    user.click()
                    expect(user).to_have_attribute("aria-pressed", "true", timeout=20000)
                    search = page.get_by_role("textbox", name="Search pages")
                    search.fill("Databases")
                    expect(page.locator(".mud-nav-menu").get_by_role("link", name="Databases", exact=True)).to_be_visible()
                    expect(page.locator(".mud-nav-menu").get_by_role("link", name="Domain management", exact=True)).to_have_count(0)
                    search.clear()
                    page.locator(".mud-nav-menu").get_by_role("link", name="Domain management", exact=True).click()
                    expect(page.get_by_role("heading", name="Domain management", exact=True)).to_be_visible()
                    page.set_viewport_size({"width": 390, "height": 844})
                    expect(page.get_by_role("button", name="Toggle navigation")).to_be_visible()
                    assert not errors, errors
                    browser.close()
                print("Interactive login, navigation context, search and responsive shell: OK")
            except BaseException:
                log.seek(0)
                print(log.read().decode(errors="replace"))
                raise
            finally:
                import signal
                try:
                    os.killpg(proc.pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
                try:
                    proc.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    os.killpg(proc.pid, signal.SIGKILL)
                    proc.wait()

if __name__ == "__main__":
    main()
