"""Install a produced ZIP into disposable Jellyfin containers and run browser tests.

Requires Docker and a temporary directory with playwright 1.56.0 installed.
Run: python3 tests/host_smoke.py --archive dist/SSO-Auth_5.0.0.0.zip --playwright /tmp/sso-browser
"""
import argparse
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile
import time
import urllib.request
import zipfile

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

def run(version, archive, playwright):
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
            state = root / "state.json"
            state.write_text(json.dumps({"base": base, "token": token, "password": password, "userId": login["User"]["Id"], "info": info, "providers": providers, "proxy": bool(prefix), "gateway": gateway}))
            state.chmod(0o600)
            result = docker("run", "--rm", "--network", "host", "-v", f"{ROOT}:/work:ro", "-v", f"{root}:/test", "-v", f"{playwright}:/driver:ro", "mcr.microsoft.com/playwright:v1.56.0-noble", "node", "/work/tests/host-browser.mjs", "/test/state.json")
            print(version + ': ' + result, flush=True)
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
    parser.add_argument('--versions', nargs='+', default=['12.0', '12.1'])
    args = parser.parse_args()
    for version in args.versions:
        run(version, args.archive.resolve(), args.playwright.resolve())
