#!/usr/bin/env python3
"""Validate an SPK; optionally exercise DSM hooks in an isolated Linux directory."""
import argparse
import hashlib
import http.cookiejar
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import io
import json
import os
from pathlib import Path
import re
import shutil
import socket
import struct
import subprocess
import tarfile
import tempfile
import threading
import time
import urllib.error
import urllib.request

checks = 0


def check(condition, message):
    global checks
    if not condition:
        raise AssertionError(message)
    checks += 1


def validate(path):
    with tarfile.open(path) as package:
        names = set(package.getnames())
        required = {"INFO", "package.tgz", "PACKAGE_ICON.PNG", "PACKAGE_ICON_256.PNG", "conf/privilege", "conf/resource"}
        check(required <= names, "Required DSM package files")
        info = dict(re.findall(r'^([a-z_]+)="(.*)"$', package.extractfile("INFO").read().decode(), re.M))
        check(info["arch"] == "x86_64" and info["os_min_ver"] == "7.2-64570", "DSM 7.2 x86_64 metadata")
        privilege = json.load(package.extractfile("conf/privilege"))
        check(privilege["defaults"]["run-as"] == "package" and "ctrl-script" not in privilege, "Unprivileged service and lifecycle hooks")
        packed = package.extractfile("package.tgz").read()
        check(hashlib.md5(packed).hexdigest() == info["checksum"], "DSM payload checksum")
        check(struct.unpack_from(">II", package.extractfile("PACKAGE_ICON.PNG").read(), 16) == (64, 64), "DSM 7 icon dimensions")
        for name in names:
            check(not name.startswith("/") and ".." not in Path(name).parts, "Safe outer archive path")
            if name.startswith("scripts/"):
                data = package.extractfile(name).read()
                check(b"\r" not in data and package.getmember(name).mode & 0o111, "Executable LF lifecycle script")
                subprocess.run(["sh", "-n"], input=data, check=True)
        with tarfile.open(fileobj=io.BytesIO(packed), mode="r:gz") as payload:
            payload_names = set(payload.getnames())
            check({"EmbyBlurayHelper", "probe.sh", "ui/config", "READ-ME.md"} <= payload_names, "Executable, launcher, UI and guide included")
            check({"LIBRARY-LANGUAGE.md", "MEDIA-FEATURES.md", "NATIVE-CHAPTERS.md", "DISC-THUMBNAILS.md", "ARCHITECTURE.md", "BRAND-ASSETS.md", "PLUGIN-UPDATES.md", "emby-plugin/EmbyBlurayHelper.Plugin.dll"} <= payload_names,
                  "ISO/feature plugin, usage and module documentation included")
            check(payload.extractfile("emby-plugin/EmbyBlurayHelper.Plugin.dll").read(2) == b"MZ", "Bundled Emby plugin is a .NET assembly")
            check({"licenses/BDInfo-LGPL-2.1.txt", "licenses/DiscUtils-MIT.txt"} <= payload_names, "All bundled library licenses included")
            check(not any(set(Path(name).parts) & {"data", "account.json", "settings.json", "probe-settings.json", "management-key.txt"} for name in payload_names), "No workstation data or credentials packaged")
            binary = payload.extractfile("EmbyBlurayHelper").read()
            check(binary[:5] == b"\x7fELF\x02" and struct.unpack_from("<H", binary, 18)[0] == 62, "Linux x86_64 binary")
            check(payload.getmember("EmbyBlurayHelper").mode == 0o755 and payload.getmember("probe.sh").mode == 0o755, "Emby can execute the binary and launcher")
            json.load(payload.extractfile("ui/config"))
        return packed


def port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def relative_loader_probe(root):
    """Reproduce Emby's ELF with the relative interpreter lib/ld-linux-*."""
    image = bytearray(Path("/bin/echo").read_bytes())
    if image[:5] != b"\x7fELF\x02":
        raise ValueError("The regression fixture requires a Linux x86_64 ELF")
    phoff = struct.unpack_from("<Q", image, 32)[0]
    entry_size, count = struct.unpack_from("<HH", image, 54)
    for index in range(count):
        header = phoff + index * entry_size
        if struct.unpack_from("<I", image, header)[0] != 3:
            continue
        offset = struct.unpack_from("<Q", image, header + 8)[0]
        size = struct.unpack_from("<Q", image, header + 32)[0]
        original_loader = bytes(image[offset:offset + size]).rstrip(b"\0").decode()
        relative_loader = b"lib/ld-linux-x86-64.so.2\0"
        if len(relative_loader) > size:
            raise ValueError("The host interpreter slot is too short")
        image[offset:offset + size] = relative_loader.ljust(size, b"\0")
        package = root / "native-emby"
        (package / "bin").mkdir(parents=True)
        (package / "lib").mkdir()
        shutil.copyfile(original_loader, package / "lib/ld-linux-x86-64.so.2")
        (package / "lib/ld-linux-x86-64.so.2").chmod(0o755)
        # Keep argv[0] as echo for distributions with multicall coreutils.
        executable = package / "bin/echo"
        executable.write_bytes(image)
        executable.chmod(0o755)
        return executable
    raise ValueError("The regression fixture has no ELF interpreter")


def lifecycle(path, packed, cross_user):
    if cross_user and os.geteuid() != 0:
        raise ValueError("--cross-user requires root; it does not install packages or create accounts")
    service_uid, service_gid = (65534, 65534) if cross_user else (os.geteuid(), os.getegid())
    probe_uid, probe_gid = (1, 1) if cross_user else (service_uid, service_gid)
    state = {"UseCustomProbePath": False, "CustomProbePath": "original-probe", "UntouchedSetting": "keep"}
    accepted_key = "fixture-api-key"

    class Emby(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def reply(self, data, status=200):
            body = json.dumps(data).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            if self.path == "/emby/System/Info/Public":
                self.reply({"ServerName": "DSM fixture", "Version": "4.10.2.0"})
            elif self.path == "/emby/Encoding/FfmpegOptions":
                self.reply({"Object": state.copy()} if self.headers.get("X-Emby-Token") == accepted_key else {},
                           200 if self.headers.get("X-Emby-Token") == accepted_key else 401)
            else:
                self.reply({}, 404)

        def do_POST(self):
            if self.path == "/emby/Encoding/FfmpegOptions" and self.headers.get("X-Emby-Token") == accepted_key:
                if self.headers.get("Transfer-Encoding") == "chunked":
                    chunks = []
                    while True:
                        size = int(self.rfile.readline().split(b";", 1)[0], 16)
                        if not size:
                            while self.rfile.readline().strip():
                                pass
                            break
                        chunks.append(self.rfile.read(size))
                        self.rfile.read(2)
                    body = b"".join(chunks)
                else:
                    body = self.rfile.read(int(self.headers["Content-Length"]))
                state.update(json.loads(body))
                self.reply({})
            else:
                self.reply({}, 401)

    backend = ThreadingHTTPServer(("127.0.0.1", 0), Emby)
    threading.Thread(target=backend.serve_forever, daemon=True).start()
    unrelated = None
    with tempfile.TemporaryDirectory(prefix="emby-dsm-check-") as temporary:
        root = Path(temporary)
        root.chmod(0o755)
        target, var, scripts = root / "target", root / "var", root / "scripts"
        target.mkdir()
        var.mkdir()
        scripts.mkdir()
        with tarfile.open(path) as package:
            for name in package.getnames():
                if name.startswith("scripts/"):
                    package.extract(name, root, filter="data")
        with tarfile.open(fileobj=io.BytesIO(packed), mode="r:gz") as payload:
            payload.extractall(target, filter="data")
        (var / "data").mkdir()
        fake = target / "fake-ffprobe"
        fake.write_text('#!/bin/sh\ncase "$1" in\n-version) echo "ffprobe version fixture";;\n-hide_banner) printf "Supported file protocols:\\nInput:\\n file\\nOutput:\\n file\\n";;\n--exit-test) echo "fixture stderr" >&2; exit 23;;\n*) printf \'{"streams":[],"format":{"fixture":true}}\\n\';;\nesac\n')
        fake.chmod(0o755)
        native_probe = relative_loader_probe(root)
        non_executable = root / "non-executable-probe"
        non_executable.write_text("#!/bin/sh\nexit 0\n")
        non_executable.chmod(0o644)
        admin_port, proxy_port = port(), port()
        while proxy_port == admin_port:
            proxy_port = port()
        settings = {"managementUrl": f"http://127.0.0.1:{admin_port}", "proxyUrl": f"http://127.0.0.1:{proxy_port}",
                    "embyUrl": f"http://127.0.0.1:{backend.server_port}", "embyApiKey": accepted_key,
                    "upstreamProbePath": str(fake), "enableProbeLanguagePatch": True, "enableInfuseLanguagePatch": True}
        settings_path = var / "data/settings.json"
        settings_path.write_text(json.dumps(settings))
        settings_path.chmod(0o600)
        if cross_user:
            for entry in [target, var, scripts, *target.rglob("*"), *var.rglob("*"), *scripts.rglob("*")]:
                os.chown(entry, service_uid, service_gid)
        environment = dict(os.environ, SYNOPKG_PKGNAME="EmbyBlurayHelper", SYNOPKG_PKGDEST=str(target),
                           SYNOPKG_PKGVAR=str(var), SYNOPKG_DSM_VERSION_MAJOR="7", SYNOPKG_DSM_VERSION_MINOR="2", SYNOPKG_PKG_STATUS="INSTALL")
        identity = {"user": service_uid, "group": service_gid, "extra_groups": []} if cross_user else {}

        def hook(name, *args, expected=0, status=None):
            env = environment.copy()
            if status is not None:
                env["SYNOPKG_PKG_STATUS"] = status
            result = subprocess.run(["sh", str(scripts / name), *args], env=env, cwd=root, capture_output=True, timeout=40, **identity)
            check(result.returncode == expected, f"{name} {args}: {result.returncode}; {result.stderr.decode(errors='replace')}")
            return result

        client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

        def api(route, body=None):
            request = urllib.request.Request(f"http://127.0.0.1:{admin_port}/api/{route}",
                data=None if body is None else json.dumps(body).encode(), headers={"Content-Type": "application/json", "X-Helper-Request": "1"})
            with client.open(request, timeout=10) as response:
                return json.load(response) if "json" in response.headers.get("Content-Type", "") else None

        def login(password="123456"):
            for attempt in range(50):
                try:
                    return api("login", {"username": "admin", "password": password})
                except (OSError, urllib.error.URLError):
                    if attempt == 49:
                        raise
                    time.sleep(.1)

        def probe(*args):
            clean_env = {key: value for key, value in os.environ.items() if not key.startswith(("EMBY_BLURAY_HELPER", "DOTNET_BUNDLE"))}
            clean_env.update(HOME=str(root / "probe-home"), DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(root / "probe-home/runtime"))
            probe_identity = {"user": probe_uid, "group": probe_gid, "extra_groups": []} if cross_user else {}
            return subprocess.run([str(target / "probe.sh"), *args], env=clean_env, cwd=root, capture_output=True, timeout=20, **probe_identity)

        probe_home = root / "probe-home"
        probe_home.mkdir(mode=0o700)
        if cross_user:
            os.chown(probe_home, probe_uid, probe_gid)
        try:
            hook("preinst")
            hook("postinst")
            hook("start-stop-status", "status", expected=3)
            hook("start-stop-status", "prestart")
            hook("start-stop-status", "start")
            hook("start-stop-status", "status")
            login()
            with client.open(f"http://127.0.0.1:{admin_port}/plugin-update/manifest.json", timeout=10) as response:
                manifest = json.load(response)
            with client.open(f"http://127.0.0.1:{admin_port}/plugin-update/EmbyBlurayHelper.Plugin.dll", timeout=10) as response:
                update_binary = response.read()
            with tarfile.open(fileobj=io.BytesIO(packed), mode="r:gz") as payload:
                check(update_binary == payload.extractfile("emby-plugin/EmbyBlurayHelper.Plugin.dll").read(),
                      "Embedded update DLL exactly matches the SPK's manually installable DLL")
            check(manifest["version"] == "0.1.8" and manifest["size"] == len(update_binary) and
                  manifest["sha256"] == hashlib.sha256(update_binary).hexdigest(), "DSM serves the verified plugin update manifest")
            with client.open(f"http://127.0.0.1:{admin_port}/", timeout=10) as response:
                overview = response.read()
            check(b'class="overview-banner"' in overview and b'/emby-icon.png' in overview and b'/infuse-icon.jpg' in overview,
                  "Packaged Linux service serves the current Emby/Infuse overview")
            for route, content_type, signature in (("emby-icon.png", "image/png", b"\x89PNG\r\n\x1a\n"),
                                                   ("infuse-icon.jpg", "image/jpeg", b"\xff\xd8\xff")):
                with client.open(f"http://127.0.0.1:{admin_port}/{route}", timeout=10) as response:
                    check(response.headers.get_content_type() == content_type and response.read().startswith(signature),
                          "Packaged overview serves embedded product artwork: " + route)
            iso = api("iso/status")
            check(not iso["canProbe"] and iso["pluginState"] == "not-loaded" and iso["probeState"] == "unsupported",
                  "DSM service detects unloaded plugin and missing HTTP/TCP input without probing movies")
            check(api("emby/test", {})["apiAuthenticated"], "Package account can reach Emby and verify key")
            state["OriginalProbePath"] = str(fake)
            discovery = api("probe/discover", {"embyUrl": settings["embyUrl"], "embyApiKey": accepted_key})
            check(discovery["path"] == str(fake) and discovery["accessible"], "Package can discover the native original probe through the Emby API")
            check(api("emby/test", {})["originalProbePath"] == str(fake), "Connection result exposes original path for automatic Web setup")
            status = api("probe/status")
            check(status["state"] == "disconnected" and status["canApply"] and not status["canRestore"], "Fresh package accurately reports unconnected Emby")
            check(api("probe/check", {})["message"] == "ffprobe version fixture", "Service account can execute original probe")
            snapshot = var / "probe-settings.json"
            check(json.loads(snapshot.read_text()) == {"upstreamProbePath": str(fake), "enableProbeLanguagePatch": True,
                  "enableMediaFeatures": False, "featureProbePath": ""}, "Shared snapshot contains only probe settings and safe disabled feature defaults")
            if cross_user:
                result = subprocess.run(["sh", "-c", 'test ! -r "$1" && test -r "$2"', "sh", str(settings_path), str(target / "probe-settings.json")], user=probe_uid, group=probe_gid, extra_groups=[])
                check(result.returncode == 0, "Emby can read snapshot but cannot read private API key")
            result = probe("-show_streams", "-of", "json")
            check(result.returncode == 0 and json.loads(result.stdout)["format"]["fixture"], "Cross-account probe produces clean ffprobe JSON")
            result = probe("--exit-test")
            check(result.returncode == 23 and result.stderr == b"fixture stderr\n", "Probe preserves upstream exit code and stderr")
            try:
                subprocess.run([str(native_probe), "-version"], cwd=root, check=True, capture_output=True)
                raise AssertionError("Relative loader fixture must fail from the wrong directory")
            except FileNotFoundError:
                check(True, "Relative ELF loader reproduces ENOENT outside the Emby root")
            settings["upstreamProbePath"] = str(native_probe)
            api("settings", settings)
            try:
                native_result = api("probe/check", {})
            except urllib.error.HTTPError as error:
                raise AssertionError("Relative-loader probe check failed: " + error.read().decode()) from error
            check(native_result["message"] == "-version", "Web probe check starts relative ELF loader from the Emby root")
            result = probe("relative-loader-success")
            check(result.returncode == 0 and result.stdout == b"relative-loader-success\n", "Emby probe invocation also starts relative ELF loader from the Emby root")
            settings["upstreamProbePath"] = str(non_executable)
            api("settings", settings)
            try:
                api("probe/check", {})
                raise AssertionError("Non-executable probe should fail")
            except urllib.error.HTTPError as error:
                check(error.code == 400 and "13" in json.load(error)["error"], "Native launch failures return actionable errors instead of HTTP 500")
            check(any("13" in entry["message"] for entry in api("logs")), "Native launch failure details are recorded in Web logs")
            settings["upstreamProbePath"] = str(fake)
            settings["enableProbeLanguagePatch"] = False
            api("settings", settings)
            check(json.loads(snapshot.read_text())["enableProbeLanguagePatch"] is False, "Web save updates shared snapshot atomically")
            api("probe/toggle", {"enabled": True})
            check(state["CustomProbePath"] == str(target / "probe.sh") and state["UntouchedSetting"] == "keep", "Emby selects launcher and preserves other encoding settings")
            status = api("probe/status")
            check(status["state"] == "connected" and status["languagePatchEnabled"] and status["canRestore"] and not status["canApply"],
                  "Turning on language patch also connects Emby and updates the shared flag")
            alias = root / "dsm-target-alias"
            alias.symlink_to(target, target_is_directory=True)
            state["CustomProbePath"] = str(alias / "probe.sh")
            check(api("probe/status")["state"] == "connected", "DSM target directory alias is recognized as the same launcher")
            api("probe/toggle", {"enabled": False})
            check(api("probe/status")["state"] == "disconnected" and not json.loads(snapshot.read_text())["enableProbeLanguagePatch"],
                  "Turning off through a DSM alias restores Emby and disables the shared flag")
            api("probe/toggle", {"enabled": True})
            api("account", {"username": "admin", "password": "retained-password"})
            original_account = (var / "data/account.json").read_bytes()
            original_settings = settings_path.read_bytes()
            hook("start-stop-status", "stop")
            check(probe("-version").returncode == 0, "Probe remains usable while the Web service is stopped")
            hook("preupgrade", status="UPGRADE")
            hook("preuninst", status="UPGRADE")
            hook("postuninst", status="UPGRADE")
            # DSM replaces target while retaining var.
            shutil.rmtree(target)
            target.mkdir()
            with tarfile.open(fileobj=io.BytesIO(packed), mode="r:gz") as payload:
                payload.extractall(target, filter="data")
            fake.write_text('#!/bin/sh\necho "ffprobe version fixture"\n')
            fake.chmod(0o755)
            if cross_user:
                for entry in [target, *target.rglob("*")]:
                    os.chown(entry, service_uid, service_gid)
            hook("postinst", status="UPGRADE")
            hook("postupgrade", status="UPGRADE")
            check((var / "data/account.json").read_bytes() == original_account and settings_path.read_bytes() == original_settings,
                  "Upgrade preserves login credentials and Emby settings")
            check(state["CustomProbePath"] == str(target / "probe.sh"), "Upgrade does not detach Emby probe")
            hook("start-stop-status", "start")
            login("retained-password")
            check(api("settings")["settings"]["embyApiKey"] == accepted_key, "Upgraded service retains saved API key")
            hook("start-stop-status", "stop")
            settings["embyApiKey"] = "invalid-key"
            settings_path.write_text(json.dumps(settings))
            hook("preuninst", expected=1, status="UNINSTALL")
            check(state["CustomProbePath"] == str(target / "probe.sh"), "Failed uninstall leaves Emby settings intact")
            settings["embyApiKey"] = accepted_key
            settings_path.write_text(json.dumps(settings))
            hook("preuninst", status="UNINSTALL")
            check(state["CustomProbePath"] == "original-probe" and state["UseCustomProbePath"] is False and state["UntouchedSetting"] == "keep",
                  "Uninstall restores original probe without changing other settings")
            unrelated = subprocess.Popen(["sleep", "60"], **identity)
            (var / "helper.pid").write_text(str(unrelated.pid))
            hook("start-stop-status", "status", expected=3)
            hook("start-stop-status", "stop")
            check(unrelated.poll() is None, "Stale PID cannot stop an unrelated process")
            # Occupied listener must fail startup and preserve the boot error.
            with socket.socket() as occupied:
                occupied.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                occupied.bind(("127.0.0.1", admin_port))
                occupied.listen()
                hook("start-stop-status", "start", expected=1)
                check((var / "startup.log").stat().st_size > 0, "Startup failure is recorded")
        finally:
            subprocess.run(["sh", str(scripts / "start-stop-status"), "stop"], env=environment, capture_output=True, timeout=40, **identity)
            if unrelated is not None:
                unrelated.terminate()
                unrelated.wait(timeout=5)
            backend.shutdown()
            backend.server_close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("spk", type=Path)
    parser.add_argument("--lifecycle", action="store_true")
    parser.add_argument("--cross-user", action="store_true")
    args = parser.parse_args()
    content = validate(args.spk)
    if args.lifecycle:
        lifecycle(args.spk.resolve(), content, args.cross_user)
    print(f"Passed {checks} Synology package checks" + (" with separate service/Emby users." if args.cross_user else "."))
