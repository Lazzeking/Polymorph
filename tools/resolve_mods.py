#!/usr/bin/env python3
"""Works out the mod files a test instance needs, for tools/test-instance.sh.

Each mod is a path to a zip or folder, or a mod id with an optional version (ardynleveling, ardynleveling@1.0.17).
A mod id is taken from your normal Mods folder when it is there, and downloaded from the Mod DB when it is not.
Dependencies are added the same way. Prints one path per line; says what it picked and why on stderr.

    resolve_mods.py <normal Mods folder> <download folder> [mod ...]
"""

import glob
import json
import os
import re
import sys
import urllib.error
import urllib.request
import zipfile

MOD_DB_API = "https://mods.vintagestory.at/api/mod/"
BUILTIN = {"game", "survival", "creative", "polymorph"}
GAME_INSTALLS = [
    "/var/lib/flatpak/app/at.vintagestory.VintageStory/current/active/files/extra/vintagestory",
    os.path.expanduser("~/.local/share/flatpak/app/at.vintagestory.VintageStory/current/active/files/extra/vintagestory"),
]


def say(message):
    sys.stderr.write(message + "\n")


def fail(message):
    say(message)
    sys.exit(1)


def modinfo(path):
    """The mod id, version and dependencies of a mod zip or folder, or (None, None, {})."""
    try:
        if os.path.isdir(path):
            with open(os.path.join(path, "modinfo.json"), encoding="utf-8-sig") as file:
                text = file.read()
        else:
            with zipfile.ZipFile(path) as archive:
                text = archive.read("modinfo.json").decode("utf-8-sig")
    except (OSError, KeyError, zipfile.BadZipFile):
        return None, None, {}

    # modinfo.json is often loose JSON with comments or trailing commas, so only the needed fields are read.
    modid = re.search(r'"modid"\s*:\s*"([^"]+)"', text, re.I)
    version = re.search(r'"version"\s*:\s*"([^"]+)"', text, re.I)
    deps = re.search(r'"dependencies"\s*:\s*\{([^}]*)\}', text, re.I | re.S)
    return (
        modid.group(1).lower() if modid else None,
        version.group(1) if version else None,
        dict(re.findall(r'"([^"]+)"\s*:\s*"([^"]*)"', deps.group(1))) if deps else {},
    )


def version_key(version):
    return [int(part) if part.isdigit() else part for part in re.split(r"[.\-+]", version or "")]


def game_version():
    for install in GAME_INSTALLS:
        for path in glob.glob(os.path.join(install, "assets", "version-*.txt")):
            return os.path.basename(path)[len("version-"):-len(".txt")]
    return None


def index(folder):
    """modid -> [(version, path)] for every mod in a folder."""
    found = {}
    if not os.path.isdir(folder):
        return found
    for name in sorted(os.listdir(folder)):
        path = os.path.join(folder, name)
        modid, version, _ = modinfo(path)
        if modid:
            found.setdefault(modid, []).append((version, path))
    return found


def pick_release(releases, version, game):
    if version:
        return next((release for release in releases if release.get("modversion") == version), None)
    if game:
        exact = next((release for release in releases if game in (release.get("tags") or [])), None)
        if exact:
            return exact
        minor = ".".join(game.split(".")[:2])
        close = next((release for release in releases if any(tag.startswith(minor) for tag in release.get("tags") or [])), None)
        if close:
            return close
    return releases[0] if releases else None


def download(modid, version, folder, game):
    try:
        with urllib.request.urlopen(MOD_DB_API + modid, timeout=30) as response:
            data = json.load(response)
    except (urllib.error.URLError, json.JSONDecodeError) as error:
        fail(f"Could not ask the Mod DB about '{modid}': {error}")

    mod = data.get("mod") if str(data.get("statuscode")) == "200" else None
    if not mod:
        fail(f"'{modid}' is not in your Mods folder and the Mod DB does not know it")

    releases = mod.get("releases") or []
    release = pick_release(releases, version, game)
    if not release:
        wanted = f"version {version}" if version else "a release"
        fail(f"The Mod DB has no {wanted} of '{modid}'. Versions there: {', '.join(r.get('modversion', '?') for r in releases[:10])}")

    os.makedirs(folder, exist_ok=True)
    path = os.path.join(folder, release["filename"])
    if not os.path.exists(path):
        say(f"Downloading {release['filename']} from the Mod DB")
        try:
            urllib.request.urlretrieve(release["mainfile"], path + ".part")
        except urllib.error.URLError as error:
            fail(f"Could not download {release['filename']}: {error}")
        os.replace(path + ".part", path)
    return path


def find(spec, local, cache, downloads, game):
    """The path for one mod, from a path, your Mods folder, earlier downloads or the Mod DB."""
    if os.path.exists(spec):
        return os.path.realpath(spec)

    modid, _, version = spec.partition("@")
    modid = modid.lower()
    for source, mods in (("your Mods folder", local), ("earlier downloads", cache)):
        candidates = [(v, p) for v, p in mods.get(modid, []) if not version or v == version]
        if candidates:
            found_version, path = max(candidates, key=lambda candidate: version_key(candidate[0]))
            say(f"{modid} {found_version} from {source}")
            return path

    path = download(modid, version or None, downloads, game)
    say(f"{modid} {modinfo(path)[1]} from the Mod DB")
    return path


def main():
    if len(sys.argv) < 3:
        fail(__doc__)

    mods_dir, downloads, specs = sys.argv[1], sys.argv[2], sys.argv[3:]
    local, cache, game = index(mods_dir), index(downloads), game_version()

    result, seen = [], set()
    queue = [find(spec, local, cache, downloads, game) for spec in specs]
    while queue:
        path = queue.pop(0)
        if path in seen:
            continue
        seen.add(path)
        result.append(path)
        modid, _, deps = modinfo(path)
        for dep in deps:
            dep = dep.lower()
            if dep not in BUILTIN and dep != modid:
                # A dependency's version is only a minimum, so any copy that is there will do.
                queue.append(find(dep, local, cache, downloads, game))

    print("\n".join(result))


if __name__ == "__main__":
    main()
