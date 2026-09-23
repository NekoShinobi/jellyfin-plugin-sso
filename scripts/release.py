"""Build release metadata and archives; publication stays in GitHub Actions."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import xml.etree.ElementTree as ET
import zipfile
from datetime import datetime, timezone


ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = (
    "SSO-Auth.dll", "Duende.IdentityModel.OidcClient.dll", "Duende.IdentityModel.dll",
    "ITfoxtec.Identity.Saml2.dll", "Microsoft.Bcl.Cryptography.dll",
    "Microsoft.IdentityModel.Abstractions.dll", "Microsoft.IdentityModel.Logging.dll",
    "Microsoft.IdentityModel.JsonWebTokens.dll", "Microsoft.IdentityModel.Tokens.dll", "Microsoft.IdentityModel.Tokens.Saml.dll",
    "Microsoft.IdentityModel.Xml.dll", "System.ServiceModel.dll",
    "System.ServiceModel.Duplex.dll", "System.ServiceModel.Primitives.dll", "System.ServiceModel.Security.dll",
)


LICENSE_FILES = ("LICENSE.txt", "THIRD-PARTY-NOTICES.txt")


def numeric_version(value):
    if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", value):
        raise ValueError("Version must contain four numeric components")
    parts = tuple(map(int, value.split(".")))
    if any(part > 65534 for part in parts):
        raise ValueError("Assembly version components must be at most 65534")
    return parts


def build_version(run_number=None):
    base = ET.parse(ROOT / "Directory.Build.props").findtext(".//Version")
    numeric_version(base)
    version = base if run_number is None else f"{base.rsplit('.', 1)[0]}.{run_number}"
    numeric_version(version)
    return version


def repository_name(value):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", value):
        raise ValueError("Repository must be OWNER/REPOSITORY")
    return value


def timestamp():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def package(build_dir, output_dir, version, repository, changelog):
    numeric_version(version)
    repository_name(repository)
    # An allowlist prevents bundling Jellyfin's own assemblies or stale build files.
    for name in ARTIFACTS:
        if not (build_dir / name).is_file():
            raise ValueError(f"Missing release dependency: {name}")
    metadata = json.loads((ROOT / "manifest.json").read_text())[0].copy()
    metadata.pop("versions")
    metadata.update(
        version=version,
        targetAbi=ET.parse(ROOT / "Directory.Build.props").findtext(".//JellyfinVersion") + ".0",
        owner=repository.split("/")[0],
        imageUrl=f"https://raw.githubusercontent.com/{repository}/main/img/sso-icon.png",
        changelog=changelog,
        timestamp=timestamp(),
    )
    output_dir.mkdir(parents=True, exist_ok=True)
    archive = output_dir / f"SSO-Auth_{version}.zip"
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as bundle:
        for name in ARTIFACTS:
            bundle.write(build_dir / name, name)
        for name in LICENSE_FILES:
            bundle.write(ROOT / name, name)
        bundle.writestr("meta.json", json.dumps(metadata, indent=2) + "\n")
    for algorithm in ("md5", "sha256"):
        digest = hashlib.new(algorithm, archive.read_bytes()).hexdigest().upper()
        archive.with_suffix(f".zip.{algorithm}").write_text(f"{digest}  {archive.name}\n")
    return archive


def update_manifest(manifest_path, archive, repository, changelog):
    repository_name(repository)
    with zipfile.ZipFile(archive) as bundle:
        if set(bundle.namelist()) != {*ARTIFACTS, *LICENSE_FILES, "meta.json"}:
            raise ValueError("Archive contains unexpected or missing files")
        metadata = json.loads(bundle.read("meta.json"))
    numeric_version(metadata["version"])
    if archive.name != f"SSO-Auth_{metadata['version']}.zip":
        raise ValueError("Archive name and metadata version differ")
    manifest = json.loads(manifest_path.read_text())
    if len(manifest) != 1 or manifest[0]["guid"] != metadata["guid"]:
        raise ValueError("Archive and catalog plugin identities differ")
    entry = dict(
        version=metadata["version"],
        targetAbi=metadata["targetAbi"],
        changelog=changelog,
        checksum=hashlib.md5(archive.read_bytes()).hexdigest().upper(),
        sourceUrl=f"https://github.com/{repository}/releases/download/{metadata['version']}/{archive.name}",
        timestamp=metadata["timestamp"],
    )
    versions = [v for v in manifest[0]["versions"] if v["version"] != entry["version"]]
    versions.append(entry)
    versions.sort(key=lambda v: numeric_version(v["version"]), reverse=True)
    manifest[0].update({k: metadata[k] for k in ("name", "description", "overview", "owner", "category", "imageUrl")})
    manifest[0]["versions"] = versions
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    version_parser = commands.add_parser("version")
    version_parser.add_argument("--run-number")
    package_parser = commands.add_parser("package")
    package_parser.add_argument("--build-dir", type=Path, default=ROOT / "SSO-Auth/bin/Release/net10.0")
    package_parser.add_argument("--output-dir", type=Path, default=ROOT / "dist")
    package_parser.add_argument("--version", required=True)
    manifest_parser = commands.add_parser("manifest")
    manifest_parser.add_argument("--manifest", type=Path, default=ROOT / "manifest.json")
    manifest_parser.add_argument("--archive", type=Path, required=True)
    for subparser in (package_parser, manifest_parser):
        subparser.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY"), required="GITHUB_REPOSITORY" not in os.environ)
        subparser.add_argument("--notes-file", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "version":
            print(build_version(args.run_number))
        elif args.command == "package":
            print(package(args.build_dir, args.output_dir, args.version, args.repository, args.notes_file.read_text()))
        else:
            update_manifest(args.manifest, args.archive, args.repository, args.notes_file.read_text())
    except (ValueError, OSError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
