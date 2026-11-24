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
                    expect(page.get_by_role("heading", name="Dashboard", exact=True)).to_be_visible()
                    # A real component event, not merely server-rendered HTML.
                    user = page.get_by_role("button", name="User", exact=True)
                    expect(user).to_be_visible()
                    # Prerendered buttons can be visible before the circuit is ready.
                    for _ in range(20):
                        user.click()
                        try:
                            expect(user).to_have_attribute("aria-pressed", "true", timeout=1000)
                            break
                        except AssertionError:
                            pass
                    else:
                        raise AssertionError("Navigation context did not become interactive")
                    search = page.get_by_placeholder("Search pages", exact=True)
                    search.fill("Databases")
                    expect(page.get_by_test_id("panel-navigation").get_by_role("link", name="Databases", exact=True)).to_be_visible()
                    expect(page.get_by_test_id("panel-navigation").get_by_role("link", name="Domain management", exact=True)).to_have_count(0)
                    search.clear()
                    page.get_by_test_id("panel-navigation").get_by_role("link", name="Domain management", exact=True).click()
                    expect(page.get_by_role("heading", name="Domain management", exact=True)).to_be_visible()
                    add = page.get_by_role("button", name="Add domain", exact=True)
                    expect(add).to_be_enabled()
                    add.click()
                    dialog = page.get_by_role("dialog")
                    expect(dialog).to_be_visible()
                    domain = dialog.locator("#newDomain")
                    expect(domain).to_be_visible()
                    submit = dialog.get_by_role("button", name="Add domain", exact=True)
                    expect(submit).to_be_disabled()
                    domain.fill("browser-test.invalid")
                    expect(submit).to_be_enabled()
                    domain.clear()
                    expect(submit).to_be_disabled()
                    dialog.get_by_role("button", name="Cancel", exact=True).click()
                    expect(dialog).to_have_count(0)
                    expect(add).to_be_enabled()
                    # Create packages in the actual UI before creating accounts.
                    for package_route, package_name in [("/user-packages", "browser-plan"), ("/reseller-packages", "browser-reseller-plan")]:
                        page.goto(base + package_route)
                        add_package = page.get_by_role("button", name="Add package", exact=True)
                        expect(add_package).to_be_enabled(timeout=20000)
                        # A full navigation initially renders static HTML. Wait for a
                        # real component event, rather than assuming enabled means interactive.
                        dialog = page.get_by_role("dialog")
                        for _ in range(20):
                            if dialog.is_visible():
                                break
                            if add_package.is_enabled():
                                add_package.click(timeout=2000)
                            try:
                                expect(dialog).to_be_visible(timeout=1000)
                                break
                            except AssertionError:
                                pass
                        else:
                            raise AssertionError(f"Package creation dialog did not open: {package_route}")
                        dialog.get_by_label("Package name", exact=True).fill(package_name)
                        dialog.get_by_role("button", name="Save", exact=True).click()
                        expect(dialog).to_have_count(0, timeout=20000)
                        expect(page.get_by_role("button", name=package_name, exact=True)).to_be_visible()
                    # Real account creation against an isolated SQLite database.
                    for route, button, account, role in [
                        ("/users", "Add user", "browseruser", "user"),
                        ("/resellers", "Create reseller", "browserreseller", "reseller"),
                    ]:
                        page.goto(base + route)
                        add_account = page.get_by_role("button", name=button, exact=True)
                        expect(add_account).to_be_enabled(timeout=20000)
                        for _ in range(20):
                            add_account.click()
                            try:
                                expect(page.get_by_role("dialog")).to_be_visible(timeout=1000)
                                break
                            except AssertionError:
                                pass
                        else:
                            raise AssertionError("Account creation dialog did not open")
                        dialog = page.get_by_role("dialog")
                        dialog.get_by_label("Account name", exact=True).fill(account)
                        dialog.get_by_label("Email address", exact=True).fill(account + "@example.invalid")
                        dialog.get_by_label("Primary domain", exact=True).fill(account + ".invalid")
                        package_name = "browser-plan" if role == "user" else "browser-reseller-plan"
                        package_select = dialog.get_by_test_id("account-package-select")
                        expect(package_select).to_be_visible()
                        package_select.click()
                        page.get_by_text(package_name, exact=True).last.click()
                        dialog.get_by_label("New password", exact=True).fill("short")
                        dialog.get_by_label("Confirm password", exact=True).fill("short")
                        dialog.get_by_role("button", name="Create account", exact=True).click()
                        expect(dialog.get_by_text("Password must contain at least 12 characters.", exact=True)).to_be_visible()
                        dialog.get_by_label("New password", exact=True).fill("ci-account-password-123!")
                        dialog.get_by_label("Confirm password", exact=True).fill("ci-account-password-123!")
                        dialog.get_by_role("button", name="Create account", exact=True).click()
                        expect(dialog).to_have_count(0, timeout=20000)
                        row = page.get_by_role("row").filter(has=page.get_by_role("link", name=account, exact=True))
                        expect(row).to_be_visible(timeout=20000)
                        expect(row.get_by_text(role, exact=True)).to_be_visible()
                        page.reload()
                        expect(row).to_be_visible(timeout=20000)
                        import sqlite3
                        with sqlite3.connect(Path(directory) / "browser.db") as db:
                            assert db.execute(
                                "SELECT COUNT(*) FROM Accounts a JOIN Domains d ON d.OwnerId=a.Id JOIN Packages p ON p.Id=a.PackageId WHERE a.Username=? AND a.Kind=? AND d.Name=? AND p.Name=?",
                                (account, role, account + ".invalid", package_name),
                            ).fetchone()[0] == 1
                    # Do not submit password mutations.
                    page.goto(base + "/change-password")
                    expect(page.get_by_role("heading", name="Change password", exact=True)).to_be_visible()
                    save = page.get_by_role("button", name="Change password", exact=True)
                    expect(save).to_be_disabled()
                    current = page.get_by_label("Current password", exact=True)
                    new = page.get_by_label("New password", exact=True)
                    repeat = page.get_by_label("Repeat new password", exact=True)
                    # Wait for interactive rendering after the full page load.
                    for _ in range(20):
                        current.fill("ci-browser-test-password")
                        new.fill("test-new-password")
                        repeat.fill("mismatch")
                        expect(save).to_be_disabled()
                        repeat.fill("test-new-password")
                        try:
                            expect(save).to_be_enabled(timeout=1000)
                            break
                        except AssertionError:
                            current.clear()
                            new.clear()
                            repeat.clear()
                    else:
                        raise AssertionError("Password form did not become interactive")
                    repeat.clear()
                    expect(save).to_be_disabled()
                    page.reload()
                    expect(page.locator(".mud-layout")).to_be_visible()
                    expect(page.get_by_role("heading", name="Change password", exact=True)).to_be_visible()
                    expect(page.get_by_label("Current password", exact=True)).to_have_value("")
                    page.set_viewport_size({"width": 390, "height": 844})
                    expect(page.get_by_role("button", name="Toggle navigation")).to_be_visible()
                    assert not errors, errors
                    browser.close()
                print("Interactive login, navigation, form guards, session restore and responsive shell: OK")
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
