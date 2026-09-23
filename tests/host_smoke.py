"""Install a produced ZIP into disposable Jellyfin containers and run browser tests.

Requires Docker and a temporary directory with playwright 1.56.0 installed.
Run: python3 tests/host_smoke.py --archive dist/SSO-Auth_5.0.0.0.zip --playwright /tmp/sso-browser
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import sqlite3
import struct
import subprocess
import tempfile
import time
import urllib.request
import zipfile
import zlib

ROOT = Path(__file__).resolve().parents[1]

def docker(*args):
    return subprocess.check_output(["docker", *args], text=True, stderr=subprocess.STDOUT).strip()

def api(base, path, data=None, token=None):
    headers = {"Content-Type": "application/json", "Authorization": 'MediaBrowser Client="SSO Smoke", Device="Test", DeviceId="sso-smoke", Version="1"' + (f', Token="{token}"' if token else '')}
    request = urllib.request.Request(base + path, None if data is None else json.dumps(data).encode(), headers)
    with urllib.request.urlopen(request, timeout=10) as response:
        body = response.read()
        return json.loads(body) if body else None

def ready(base):
    for _ in range(90):
        try:
            info = api(base, "/System/Info/Public")
            if "Version" in info: return info
            time.sleep(1)
        except Exception:
            time.sleep(1)
    raise RuntimeError("Jellyfin did not become ready")

def seed_legacy_avatar(base, token, config, name):
    # Only this disposable host is modified; the real uploader establishes the
    # metadata relationship before we reproduce the historical missing-dot bug.
    user = api(base, "/Users/New", {"Name": "avatar-recovery-user"}, token)
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 16, 16, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress((b"\x00" + b"\x20\x80\xc0" * 16) * 16)) + chunk(b"IEND", b"")
    upload = urllib.request.Request(base + "/UserImage?userId=" + user["Id"], base64.b64encode(png),
                                    {"Content-Type": "image/png", "Authorization": f'MediaBrowser Token="{token}"'})
    with urllib.request.urlopen(upload, timeout=10) as response:
        assert response.status == 204
    files = list(config.rglob("profile.png"))
    assert len(files) == 1, "Expected only the synthetic user's uploaded avatar"
    source = files[0]
    original = "/config/" + source.relative_to(config).as_posix()
    legacy = source.with_name("profilepng")
    legacy_path = "/config/" + legacy.relative_to(config).as_posix()
    docker("stop", name)
    source.rename(legacy)
    with sqlite3.connect(config / "data/jellyfin.db") as database:
        changed = database.execute('UPDATE ImageInfos SET Path = ? WHERE Path = ?', (legacy_path, original))
        assert changed.rowcount == 1
    docker("start", name)
    return {"userId": user["Id"], "legacyPath": legacy.relative_to(config).as_posix()}, png

def dependency_evidence(name, version, archive, evidence_dir):
    """Record package bytes and DLLs actually mapped by the exercised Linux host."""
    with zipfile.ZipFile(archive) as bundle:
        packaged = {path: hashlib.sha256(bundle.read(path)).hexdigest()
                    for path in bundle.namelist() if path.endswith('.dll')}
    maps = docker("exec", name, "cat", "/proc/1/maps")
    paths = sorted({line.split(maxsplit=5)[5] for line in maps.splitlines()
                    if len(line.split(maxsplit=5)) == 6 and line.endswith('.dll')})
    assert any(Path(path).name == 'SSO-Auth.dll' for path in paths), "Plugin assembly was not mapped by the host"
    loaded = []
    for line in docker("exec", name, "sha256sum", *paths).splitlines():
        digest, path = line.split(maxsplit=1)
        loaded.append({"path": path, "sha256": digest,
                       "matchesPackage": packaged.get(Path(path).name) == digest})
    evidence_dir.mkdir(parents=True, exist_ok=True)
    (evidence_dir / f"host-{version}-assemblies.json").write_text(json.dumps({
        "host": version, "archive": archive.name,
        "archiveSha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
        "packaged": packaged, "loaded": loaded,
    }, indent=2) + "\n")
    print(f"{version}: recorded {len(packaged)} packaged and {len(loaded)} host-mapped assemblies", flush=True)


def run(version, archive, playwright, evidence_dir):
    name = "sso-rewrite-" + version.replace('.', '-') + '-' + secrets.token_hex(3)
    with tempfile.TemporaryDirectory(prefix="sso-integration-") as directory:
        root = Path(directory)
        config = root / "config"
        prefix = "/jellyfin" if version == "12.1" else ""
        gateway = json.loads(docker("network", "inspect", "bridge"))[0]["IPAM"]["Config"][0]["Gateway"]
        (config / "config").mkdir(parents=True)
        (config / "config/network.xml").write_text(f'<NetworkConfiguration><BaseUrl>{prefix}</BaseUrl><KnownProxies><string>{gateway}</string></KnownProxies></NetworkConfiguration>')
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", str(root / "proxy.key"), "-out", str(root / "proxy.crt"), "-days", "1", "-subj", "/CN=localhost"], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        with zipfile.ZipFile(archive) as bundle:
            package_version = json.loads(bundle.read("meta.json"))["version"]
        plugin = config / ("plugins/SSO-Auth_" + package_version)
        plugin.mkdir(parents=True)
        (root / "cache").mkdir()
        with zipfile.ZipFile(archive) as bundle:
            bundle.extractall(plugin)
        configs = config / "plugins/configurations"
        configs.mkdir()
        shutil.copy(ROOT / "tests/SSO-Auth.Tests/Fixtures/legacy-4.xml", configs / "SSO-Auth.xml")
        try:
            docker("run", "-d", "--name", name, "--user", f"{os.getuid()}:{os.getgid()}",
                   "-p", "127.0.0.1::8096", "-v", f"{config}:/config", "-v", f"{root / 'cache'}:/cache", f"jellyfin/jellyfin:{version}")
            port = docker("port", name, "8096/tcp").rsplit(':', 1)[1]
            base = "http://127.0.0.1:" + port + prefix
            info = ready(base)
            assert info["Version"].startswith(version)
            api(base, "/Startup/User")
            password = secrets.token_urlsafe(24)
            api(base, "/Startup/User", {"Name": "local-admin", "Password": password})
            api(base, "/Startup/Complete", {})
            login = api(base, "/Users/AuthenticateByName", {"Username": "local-admin", "Pw": password})
            token = login["AccessToken"]
            providers = api(base, "/Auth/Providers", token=token)
            assert any(p["Id"] == "Jellyfin.Plugin.SSO_Auth.Services.SsoAuthenticationProvider" for p in providers), providers
            old = api(base, "/sso/OID/Get", token=token)["Case-Sensitive"]
            assert old["OidSecret"] == "synthetic-secret"
            assert old["CanonicalLinks"]["legacy-name"].replace('-', '') == '1' * 32
            assert (configs / "SSO-Auth.xml.pre-v1.bak").read_bytes() == (ROOT / "tests/SSO-Auth.Tests/Fixtures/legacy-4.xml").read_bytes()
            avatar, original_avatar = seed_legacy_avatar(base, token, config, name)
            port = docker("port", name, "8096/tcp").rsplit(':', 1)[1]
            base = "http://127.0.0.1:" + port + prefix
            info = ready(base)
            state = root / "state.json"
            state.write_text(json.dumps({"base": base, "token": token, "password": password, "userId": login["User"]["Id"], "info": info, "providers": providers, "proxy": bool(prefix), "gateway": gateway, "avatarRecovery": avatar}))
            state.chmod(0o600)
            result = docker("run", "--rm", "--network", "host", "-v", f"{ROOT}:/work:ro", "-v", f"{root}:/test", "-v", f"{playwright}:/driver:ro", "mcr.microsoft.com/playwright:v1.56.0-noble", "node", "/work/tests/host-browser.mjs", "/test/state.json")
            print(version + ': ' + result, flush=True)
            dependency_evidence(name, version, archive, evidence_dir)
            legacy_user = next(user for user in api(base, "/Users", token=token) if user["Name"] == "browser-user")
            legacy_policy = legacy_user["Policy"].copy()
            legacy_policy["AuthenticationProviderId"] = "Jellyfin.Plugin.SSO_Auth.Api.SSOController"
            api(base, "/Users/" + legacy_user["Id"] + "/Policy", legacy_policy, token)
            docker("restart", name)
            port = docker("port", name, "8096/tcp").rsplit(":", 1)[1]
            base = "http://127.0.0.1:" + port + prefix
            ready(base)
            assert api(base, "/sso/OID/Get", token=token)["Case-Sensitive"]["OidSecret"] == "synthetic-secret"
            repaired = api(base, "/Users/" + legacy_user["Id"], token=token)
            assert repaired["Id"] == legacy_user["Id"]
            assert repaired["Policy"]["AuthenticationProviderId"] == "Jellyfin.Plugin.SSO_Auth.Services.SsoAuthenticationProvider"
            assert (configs / "SSO-Auth.xml.pre-v1.bak").read_bytes() == (ROOT / "tests/SSO-Auth.Tests/Fixtures/legacy-4.xml").read_bytes()
            api(base, "/Users/AuthenticateByName", {"Username": "local-admin", "Pw": password})
            user_storage = (config / avatar["legacyPath"]).parent.parent
            recovered = list((user_storage / avatar["userId"].replace("-", "")).glob("profile-recovered-*.png"))
            assert len(recovered) == 1, "Repeated login must not create more recovery copies"
            assert recovered[0].read_bytes() == original_avatar
            assert (config / avatar["legacyPath"]).read_bytes() == original_avatar
            recovered_path = "/config/" + recovered[0].relative_to(config).as_posix()
            with sqlite3.connect(config / "data/jellyfin.db") as database:
                assert database.execute('SELECT COUNT(*) FROM ImageInfos WHERE Path = ?', (recovered_path,)).fetchone()[0] == 1
                assert database.execute('SELECT COUNT(*) FROM ImageInfos WHERE Path = ?', ("/config/" + avatar["legacyPath"],)).fetchone()[0] == 0
            avatar_request = urllib.request.Request(base + "/UserImage?userId=" + avatar["userId"] + "&format=png")
            with urllib.request.urlopen(avatar_request, timeout=10) as response:
                assert response.status == 200 and response.headers.get_content_type() == "image/png"
                assert response.read().startswith(b"\x89PNG\r\n\x1a\n")
            print(version + ': legacy avatar repaired, original preserved, and image renders after restart', flush=True)
            logs = docker("logs", name)
            for bad in ("Could not load file or assembly", "ReflectionTypeLoadException", "AmbiguousMatchException", "Unable to resolve service", "invalid authentication provider", "synthetic-secret"):
                assert bad.lower() not in logs.lower(), f"Unexpected host log content: {bad if bad != token else 'credential'}"
            plugin_logs = "\n".join(line for line in logs.splitlines() if "Jellyfin.Plugin.SSO_Auth" in line)
            assert token not in plugin_logs and "synthetic-access" not in plugin_logs
            print(version + ': restart, migration backup, local login, dependency loading, and redacted logs passed', flush=True)
        except Exception as error:
            # Keep failures inspectable without putting host credentials into tool output.
            failure = Path('/tmp') / (name + '-failure')
            shutil.copytree(root, failure)
            if isinstance(error, subprocess.CalledProcessError):
                detail = error.output.replace(locals().get("token", "no-token"), "[redacted]")
                (failure / "browser.log").write_text(detail)
                print(detail, flush=True)
            try: (failure / 'host.log').write_text(docker('logs', name))
            except Exception: pass
            print(f"Failure evidence saved to {failure}", flush=True)
            raise
        finally:
            try: docker("rm", "-f", name)
            except subprocess.CalledProcessError: pass

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--archive', type=Path, required=True)
    parser.add_argument('--playwright', type=Path, required=True)
    parser.add_argument('--evidence-dir', type=Path, default=ROOT / 'dist/dependency-evidence')
    parser.add_argument('--versions', nargs='+', default=['12.0', '12.1'])
    args = parser.parse_args()
    for version in args.versions:
        run(version, args.archive.resolve(), args.playwright.resolve(), args.evidence_dir.resolve())
