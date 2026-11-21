#!/usr/bin/env bash
# ==============================================================================
# eng/lock/generate-build-inputs.sh
#
# Generates the complete, pinned build inputs closure (config/build-inputs.json)
# for the Serpy offline Debian 13 appliance build.
#
# Designed to run in Debian 13 (Trixie) or a `debian:trixie` Docker container
# with internet access.
#
# Usage:
#   ./eng/lock/generate-build-inputs.sh [--output <path>] [--workdir <dir>]
# ==============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

OUTPUT_FILE="${REPO_ROOT}/config/build-inputs.json"
WORKDIR="/srv"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --output)
      OUTPUT_FILE="$2"
      shift 2
      ;;
    --workdir)
      WORKDIR="$2"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

if [[ $EUID -ne 0 ]]; then
  echo "Error: This script must be run as root." >&2
  exit 1
fi

export DEBIAN_FRONTEND=noninteractive
printf '%s\n' '#!/bin/sh' 'exit 101' > /usr/sbin/policy-rc.d
chmod 0755 /usr/sbin/policy-rc.d

echo "==> Setting up workspace under $WORKDIR"
CACHE_DIR="${WORKDIR}/cache"
mkdir -p "$CACHE_DIR/debs" "$CACHE_DIR/git" "$CACHE_DIR/tools" "$CACHE_DIR/wheels" "$CACHE_DIR/yarn-mirror"

# 1. Base prerequisites
echo "==> Installing host prerequisites..."
apt-get update -q
apt-get install -y --no-install-recommends \
  ca-certificates curl git python3 python3-pip jq xz-utils dpkg-dev

# 2. Configure Snapshot Repositories & MariaDB
echo "==> Configuring apt repositories and preferences..."
rm -f /etc/apt/sources.list /etc/apt/sources.list.d/*.list /etc/apt/sources.list.d/*.sources
printf '%s\n' 'deb [check-valid-until=no] https://snapshot.debian.org/archive/debian/20260822T000000Z/ trixie main' > /etc/apt/sources.list.d/serpy-trixie.list
printf '%s\n' 'Acquire::Check-Valid-Until "false";' > /etc/apt/apt.conf.d/99serpy-snapshot
printf '%s\n' 'deb [check-valid-until=no] https://snapshot.debian.org/archive/debian/20260822T000000Z/ sid main' > /etc/apt/sources.list.d/serpy-python314.list
printf '%s\n' \
  'Package: *' 'Pin: release a=unstable' 'Pin-Priority: 1' '' \
  'Package: python3.14 python3.14-dev python3.14-venv libpython3.14 libpython3.14-minimal libpython3.14-stdlib libpython3.14-dev' \
  'Pin: release a=unstable' 'Pin-Priority: 1001' > /etc/apt/preferences.d/serpy-python314
printf '%s\n' 'deb [check-valid-until=no] https://snapshot.debian.org/archive/debian-security/20260822T000000Z/ trixie-security main' > /etc/apt/sources.list.d/serpy-trixie-security.list
printf '%s\n' 'deb [check-valid-until=no] https://snapshot.debian.org/archive/debian/20260822T000000Z/ trixie-updates main' > /etc/apt/sources.list.d/serpy-trixie-updates.list

install -d -m 0755 /etc/apt/keyrings
curl -fsSL https://supplychain.mariadb.com/mariadb-keyring-2025.gpg -o /etc/apt/keyrings/mariadb-keyring.gpg
printf '%s\n' 'deb [signed-by=/etc/apt/keyrings/mariadb-keyring.gpg] https://archive.mariadb.org/mariadb-11.8.3/repo/debian trixie main' > /etc/apt/sources.list.d/serpy-mariadb.list
printf '%s\n' 'Package: mariadb-* libmariadb* libmariadbd* galera-4 mysql-common' 'Pin: origin archive.mariadb.org' 'Pin-Priority: 1001' > /etc/apt/preferences.d/serpy-mariadb

apt-get update -q

# 3. Collect Debian Packages
echo "==> Collecting Debian debs..."
apt-get clean
apt-get install -y --download-only \
  git cron fontconfig qemu-guest-agent qemu-utils nginx supervisor python3-pip build-essential \
  libssl-dev libffi-dev libmariadb-dev libmariadb-dev-compat libxml2-dev libxslt-dev libjpeg-dev zlib1g-dev libcairo2-dev \
  redis-server=5:8.0.2-3+deb13u2 mariadb-server=1:11.8.3+maria~deb13
apt-get install -y --download-only -t sid \
  python3.14=3.14.7-1 python3.14-dev=3.14.7-1 python3.14-venv=3.14.7-1

cp -a /var/cache/apt/archives/*.deb "$CACHE_DIR/debs/"

# 4. Install toolchain for bench & frontend
echo "==> Installing toolchain for asset and wheel resolution..."
apt-get install -y \
  git cron fontconfig nginx supervisor build-essential \
  libssl-dev libffi-dev libmariadb-dev libmariadb-dev-compat libxml2-dev libxslt-dev libjpeg-dev zlib1g-dev libcairo2-dev \
  redis-server=5:8.0.2-3+deb13u2 mariadb-server=1:11.8.3+maria~deb13
apt-get install -y -t sid \
  python3.14=3.14.7-1 python3.14-dev=3.14.7-1 python3.14-venv=3.14.7-1

# 5. Collect Node & Yarn
echo "==> Collecting Node and Yarn..."
NODE_URL="https://nodejs.org/dist/v24.2.0/node-v24.2.0-linux-x64.tar.xz"
curl -fsSLo "$CACHE_DIR/tools/node.tar.xz" "$NODE_URL"
echo '91a0794f4dbc94bc4a9296139ed9101de21234982bae2b325e37ebd3462273e5  '"$CACHE_DIR/tools/node.tar.xz" | sha256sum -c -

tar -C /usr/local --strip-components=1 -xJf "$CACHE_DIR/tools/node.tar.xz"
YARN_URL="https://registry.yarnpkg.com/yarn/-/yarn-1.22.22.tgz"
curl -fsSLo "$CACHE_DIR/tools/yarn-1.22.22.tgz" "$YARN_URL"
echo 'c17d3797fb9a9115bf375e31bfd30058cac6bc9c3b8807a3d8cb2094794b51ca  '"$CACHE_DIR/tools/yarn-1.22.22.tgz" | sha256sum -c -

npm install -g "$CACHE_DIR/tools/yarn-1.22.22.tgz"

# 6. Collect Git Tarballs
echo "==> Downloading pinned Git repository tarballs..."
curl -fsSLo "$CACHE_DIR/git/frappe.tar.gz" "https://github.com/frappe/frappe/archive/6a329d068416768ec47ccd3326b9cc95a8d7bf99.tar.gz"
echo 'bf99617bea31102647ef5d67aa953e69df6e9ce6148a7ee4993df8b5d1080766  '"$CACHE_DIR/git/frappe.tar.gz" | sha256sum -c -

curl -fsSLo "$CACHE_DIR/git/erpnext.tar.gz" "https://github.com/frappe/erpnext/archive/11e0ba0a1c45f217e2e73e885f699102d06da325.tar.gz"
echo 'f9a9c2a897c0b79cf91d93e6b17049c9e4d6ff4f99059ce05787ce2c4364b7af  '"$CACHE_DIR/git/erpnext.tar.gz" | sha256sum -c -

curl -fsSLo "$CACHE_DIR/git/pypika.tar.gz" "https://github.com/frappe/pypika/archive/2c50e6142b2d61d2d243e466fdd5dc03b3d918f2.tar.gz"
echo '59cc0e2a77082cf3f1793a737fc78c012f500644769b7fe10c29819c16b05941  '"$CACHE_DIR/git/pypika.tar.gz" | sha256sum -c -

curl -fsSLo "$CACHE_DIR/git/gunicorn.tar.gz" "https://github.com/frappe/gunicorn/archive/54b59ca884c79dcf04d4882658a32bbaf17e999f.tar.gz"
echo 'ab6e7eef3e2fc6fa0b7681f0bc3966e379c9cf30a8a73407ab03d7aafad99560  '"$CACHE_DIR/git/gunicorn.tar.gz" | sha256sum -c -

# 7. Collect Wheels & Yarn Mirror
echo "==> Initializing bench environment and collecting wheels & yarn packages..."
python3.14 -m venv /opt/serpy-tools
/opt/serpy-tools/bin/pip install --no-cache-dir frappe-bench==5.31.0 uv==0.11.33
ln -sf /opt/serpy-tools/bin/bench /usr/local/bin/bench
ln -sf /opt/serpy-tools/bin/uv /usr/local/bin/uv

id -u frappe >/dev/null 2>&1 || useradd -m -s /bin/bash frappe
su - frappe -c "bench init --frappe-branch v16.31.0 --python python3.14 /home/frappe/frappe-bench"
su - frappe -c "cd /home/frappe/frappe-bench/apps/frappe && git checkout --detach 6a329d068416768ec47ccd3326b9cc95a8d7bf99"
su - frappe -c "bench get-app --branch v16.32.3 erpnext"
su - frappe -c "cd /home/frappe/frappe-bench/apps/erpnext && git checkout --detach 11e0ba0a1c45f217e2e73e885f699102d06da325"

# Collect yarn mirror
export YARN_CACHE_FOLDER="$CACHE_DIR/yarn-mirror"
for APP_DIR in /home/frappe/frappe-bench/apps/frappe /home/frappe/frappe-bench/apps/erpnext /home/frappe/frappe-bench/apps/erpnext/banking; do
  if [ -f "$APP_DIR/yarn.lock" ]; then
    ( cd "$APP_DIR" && yarn install --frozen-lockfile --cache-folder "$CACHE_DIR/yarn-mirror" )
  fi
done

# Collect wheels
/home/frappe/frappe-bench/env/bin/pip cache list --format=abspath 2>/dev/null | while read -r line; do
  if [ -f "$line" ]; then cp "$line" "$CACHE_DIR/wheels/"; fi
done || true

find /root/.cache/pip/wheels /home/frappe/.cache/pip/wheels -type f \( -name "*.whl" -o -name "*.tar.gz" \) -exec cp {} "$CACHE_DIR/wheels/" \; 2>/dev/null || true

# Download additional wheels directly into cache
/home/frappe/frappe-bench/env/bin/pip download --dest "$CACHE_DIR/wheels" \
  frappe-bench==5.31.0 uv==0.11.33 \
  setuptools==78.1.1 wheel==0.46.1 flit-core==3.11.0 hatchling==1.27.0 pillow==12.2.0 pdfplumber==0.11.10 \
  docopt==0.6.2 googlemaps==4.10.0 mysqlclient==2.2.7 plaid-python==7.2.1 PyQRCode==1.2.1 rauth==0.7.3 psutil==7.0.0

# 8. Assemble config/build-inputs.json
echo "==> Assembling build inputs JSON metadata..."
python3 - <<PYEOF
import os, glob, json, hashlib, base64, urllib.request, re, subprocess

# 1. Packages from apt lists
pkg_lists = glob.glob("/var/lib/apt/lists/*_Packages")
pkg_map = {}
for pl in pkg_lists:
    origin = "debian"
    if "mariadb" in pl:
        origin = "mariadb"
    elif "debian-security" in pl:
        origin = "debian-security"
    elif "sid" in pl or "unstable" in pl:
        origin = "debian-sid"
        
    with open(pl, "r", encoding="utf-8", errors="ignore") as f:
        p_name = None; p_ver = None; p_arch = None; p_fn = None; p_sz = None; p_sha = None
        for line in f:
            line = line.strip()
            if line.startswith("Package: "): p_name = line.split("Package: ", 1)[1]
            elif line.startswith("Version: "): p_ver = line.split("Version: ", 1)[1]
            elif line.startswith("Architecture: "): p_arch = line.split("Architecture: ", 1)[1]
            elif line.startswith("Filename: "): p_fn = line.split("Filename: ", 1)[1]
            elif line.startswith("Size: "): p_sz = int(line.split("Size: ", 1)[1])
            elif line.startswith("SHA256: "): p_sha = line.split("SHA256: ", 1)[1]
            elif line == "":
                if p_name and p_ver and p_arch and p_fn and p_sz and p_sha:
                    pkg_map[(p_name, p_ver, p_arch)] = (origin, p_fn, p_sz, p_sha)
                p_name = None; p_ver = None; p_arch = None; p_fn = None; p_sz = None; p_sha = None

db_files = []
sys_files = []
lang_debs = []

cache_debs = sorted(glob.glob("$CACHE_DIR/debs/*.deb"))
for cd in cache_debs:
    base = os.path.basename(cd)
    out = subprocess.check_output(["dpkg-deb", "-f", cd, "Package", "Version", "Architecture"]).decode()
    fields = {}
    for l in out.strip().split("\n"):
        if ": " in l:
            k, v = l.split(": ", 1)
            fields[k] = v
    pkg_name = fields.get("Package")
    pkg_ver = fields.get("Version")
    pkg_arch = fields.get("Architecture")
    key_tuple = (pkg_name, pkg_ver, pkg_arch)
    if key_tuple not in pkg_map:
        raise Exception(f"Missing deb metadata for {base} {key_tuple}")
        
    origin, p_fn, p_sz, p_sha = pkg_map[key_tuple]
    with open(cd, "rb") as f:
        actual_sha = hashlib.sha256(f.read()).hexdigest()
    actual_sz = os.path.getsize(cd)
    assert actual_sha == p_sha, f"SHA mismatch on {base}"
    assert actual_sz == p_sz, f"Size mismatch on {base}"
    
    urls = []
    if origin == "mariadb":
        urls = [f"https://archive.mariadb.org/mariadb-11.8.3/repo/debian/{p_fn}"]
    elif origin == "debian-security":
        urls = [
            f"https://deb.debian.org/debian-security/{p_fn}",
            f"https://snapshot.debian.org/archive/debian-security/20260822T000000Z/{p_fn}"
        ]
    else:
        urls = [
            f"https://deb.debian.org/debian/{p_fn}",
            f"https://snapshot.debian.org/archive/debian/20260822T000000Z/{p_fn}"
        ]
        
    file_obj = {
        "key": base,
        "path": f"debs/{base}",
        "size": actual_sz,
        "sha256": actual_sha,
        "sha512": None,
        "urls": urls,
        "inGuest": True
    }
    
    if origin == "mariadb" or any(pkg_name.startswith(p) for p in ["mariadb-", "libmariadb", "galera-", "mysql-common"]):
        db_files.append(file_obj)
    elif pkg_name.startswith("python3.14") or pkg_name.startswith("libpython3.14"):
        lang_debs.append(file_obj)
    else:
        sys_files.append(file_obj)

lang_wheels = []
wheel_files = sorted(glob.glob("$CACHE_DIR/wheels/*"))
for wf in wheel_files:
    fn = os.path.basename(wf)
    if fn.startswith("gunicorn-23") or fn.startswith("PyPika-0.48"):
        continue
    with open(wf, "rb") as f:
        actual_sha = hashlib.sha256(f.read()).hexdigest()
    actual_sz = os.path.getsize(wf)
    
    pkg = None; ver = None
    if fn.endswith(".whl"):
        parts = fn[:-4].split("-")
        pkg = parts[0]; ver = parts[1]
    elif fn.endswith(".tar.gz"):
        m = re.match(r"^(.+)-(\d.*)\.tar\.gz$", fn)
        if m: pkg, ver = m.group(1), m.group(2)
    elif fn.endswith(".zip"):
        m = re.match(r"^(.+)-(\d.*)\.zip$", fn)
        if m: pkg, ver = m.group(1), m.group(2)
        
    pypi_url = None
    try:
        url = f"https://pypi.org/pypi/{pkg}/{ver}/json"
        req = urllib.request.Request(url, headers={"User-Agent": "SerpyLockGenerator/1.0"})
        with urllib.request.urlopen(req, timeout=10) as r:
            data = json.loads(r.read())
            for u in data.get("urls", []):
                if u["filename"] == fn:
                    pypi_url = u["url"]
                    break
    except Exception as e:
        print(f"Failed to fetch pypi for {fn}: {e}")
        raise
        
    assert pypi_url is not None, f"No PyPI URL found for {fn}"
    lang_wheels.append({
        "key": fn,
        "path": f"wheels/{fn}",
        "size": actual_sz,
        "sha256": actual_sha,
        "sha512": None,
        "urls": [pypi_url],
        "inGuest": True
    })

tool_files = [
    {
        "key": "node.tar.xz",
        "path": "tools/node.tar.xz",
        "size": os.path.getsize("$CACHE_DIR/tools/node.tar.xz"),
        "sha256": hashlib.sha256(open("$CACHE_DIR/tools/node.tar.xz", "rb").read()).hexdigest(),
        "sha512": None,
        "urls": ["https://nodejs.org/dist/v24.2.0/node-v24.2.0-linux-x64.tar.xz"],
        "inGuest": True
    },
    {
        "key": "yarn-1.22.22.tgz",
        "path": "tools/yarn-1.22.22.tgz",
        "size": os.path.getsize("$CACHE_DIR/tools/yarn-1.22.22.tgz"),
        "sha256": hashlib.sha256(open("$CACHE_DIR/tools/yarn-1.22.22.tgz", "rb").read()).hexdigest(),
        "sha512": None,
        "urls": ["https://registry.yarnpkg.com/yarn/-/yarn-1.22.22.tgz"],
        "inGuest": True
    }
]

lang_files = lang_debs + lang_wheels + tool_files

yarn_lock_files = [
    "/home/frappe/frappe-bench/apps/frappe/yarn.lock",
    "/home/frappe/frappe-bench/apps/erpnext/yarn.lock",
    "/home/frappe/frappe-bench/apps/erpnext/banking/yarn.lock"
]
yarn_map = {}
for yf in yarn_lock_files:
    if not os.path.exists(yf): continue
    with open(yf, "r", encoding="utf-8") as f:
        curr_u = None; curr_i = None
        for line in f:
            line = line.strip()
            if line.startswith("resolved "):
                curr_u = line.split(" ", 1)[1].strip('"').split("#")[0]
            elif line.startswith("integrity "):
                curr_i = line.split(" ", 1)[1].strip('"')
                if curr_u and curr_i:
                    fn = os.path.basename(curr_u)
                    if "/@" in curr_u:
                        parts = curr_u.split("/")
                        idx = [i for i, p in enumerate(parts) if p.startswith("@")][0]
                        fn = f"{parts[idx]}-{parts[-1]}"
                    yarn_map[fn] = (curr_u, curr_i)
                    curr_u = None; curr_i = None

web_files = []
mirror_files = sorted(glob.glob("$CACHE_DIR/yarn-mirror/*.tgz"))
for mf in mirror_files:
    base = os.path.basename(mf)
    assert base in yarn_map, f"Missing yarn.lock entry for {base}"
    u, integ = yarn_map[base]
    with open(mf, "rb") as f:
        data = f.read()
    actual_sha512 = hashlib.sha512(data).hexdigest()
    actual_sz = len(data)
    
    web_files.append({
        "key": base,
        "path": f"yarn-mirror/{base}",
        "size": actual_sz,
        "sha256": None,
        "sha512": actual_sha512,
        "urls": [u],
        "inGuest": True
    })

framework_files = [
    {
        "key": "frappe.tar.gz",
        "path": "git/frappe.tar.gz",
        "size": os.path.getsize("$CACHE_DIR/git/frappe.tar.gz"),
        "sha256": hashlib.sha256(open("$CACHE_DIR/git/frappe.tar.gz", "rb").read()).hexdigest(),
        "sha512": None,
        "urls": ["https://github.com/frappe/frappe/archive/6a329d068416768ec47ccd3326b9cc95a8d7bf99.tar.gz"],
        "inGuest": True
    },
    {
        "key": "pypika.tar.gz",
        "path": "git/pypika.tar.gz",
        "size": os.path.getsize("$CACHE_DIR/git/pypika.tar.gz"),
        "sha256": hashlib.sha256(open("$CACHE_DIR/git/pypika.tar.gz", "rb").read()).hexdigest(),
        "sha512": None,
        "urls": ["https://github.com/frappe/pypika/archive/2c50e6142b2d61d2d243e466fdd5dc03b3d918f2.tar.gz"],
        "inGuest": True
    },
    {
        "key": "gunicorn.tar.gz",
        "path": "git/gunicorn.tar.gz",
        "size": os.path.getsize("$CACHE_DIR/git/gunicorn.tar.gz"),
        "sha256": hashlib.sha256(open("$CACHE_DIR/git/gunicorn.tar.gz", "rb").read()).hexdigest(),
        "sha512": None,
        "urls": ["https://github.com/frappe/gunicorn/archive/54b59ca884c79dcf04d4882658a32bbaf17e999f.tar.gz"],
        "inGuest": True
    }
]

erpnext_files = [
    {
        "key": "erpnext.tar.gz",
        "path": "git/erpnext.tar.gz",
        "size": os.path.getsize("$CACHE_DIR/git/erpnext.tar.gz"),
        "sha256": hashlib.sha256(open("$CACHE_DIR/git/erpnext.tar.gz", "rb").read()).hexdigest(),
        "sha512": None,
        "urls": ["https://github.com/frappe/erpnext/archive/11e0ba0a1c45f217e2e73e885f699102d06da325.tar.gz"],
        "inGuest": True
    }
]

engine_files = [
    {
        "key": "qemu-w64-setup-20260811.exe",
        "path": "",
        "size": 188743680,
        "sha256": "f98a8aeb5f7faea9765b6dee28316c266cd179d80354a2fed8e50176f9a2e59f",
        "sha512": None,
        "urls": ["https://qemu.weilnetz.de/w64/qemu-w64-setup-20260811.exe"],
        "inGuest": False
    }
]

os_files = [
    {
        "key": "debian-13-genericcloud-amd64-20260819-2575.qcow2",
        "path": "",
        "size": 357564416,
        "sha256": None,
        "sha512": "77429b411b39b43f914dc9d14bf34aa315489a1a12b5429f72e5b483bdda23c65698d33443c85d3f3ad7c3a0828ae60845406d6b99646342554d17abae29c2a3",
        "urls": ["https://cloud.debian.org/images/cloud/trixie/20260819-2575/debian-13-genericcloud-amd64-20260819-2575.qcow2"],
        "inGuest": False
    }
]

parts = [
    {"id": "engine", "title": "Serpy engine", "files": engine_files},
    {"id": "os", "title": "Operating system for ERPNext", "files": os_files},
    {"id": "database", "title": "Database", "files": db_files},
    {"id": "system-tools", "title": "System tools", "files": sys_files},
    {"id": "language-tools", "title": "Language tools", "files": lang_files},
    {"id": "web-tools", "title": "Web tools", "files": web_files},
    {"id": "framework", "title": "ERPNext framework", "files": framework_files},
    {"id": "erpnext", "title": "ERPNext", "files": erpnext_files}
]

doc = {
    "$comment": "Complete pinned build inputs closure for Serpy Debian 13 / ERPNext v16 appliance.",
    "schemaVersion": 1,
    "versions": {
        "pythonLock": "3.14.7",
        "nodeLock": "24.2.0",
        "mariadbLock": "11.8.3",
        "redisLock": "8.0.2",
        "frappeCommit": "6a329d068416768ec47ccd3326b9cc95a8d7bf99",
        "erpnextCommit": "11e0ba0a1c45f217e2e73e885f699102d06da325",
        "frappeLock": "16.31.0",
        "erpnextLock": "16.32.3"
    },
    "parts": parts
}

os.makedirs(os.path.dirname(os.path.abspath("$OUTPUT_FILE")), exist_ok=True)
with open("$OUTPUT_FILE", "w", encoding="utf-8") as f:
    json.dump(doc, f, indent=2)

print(f"Successfully generated $OUTPUT_FILE ({sum(len(p['files']) for p in parts)} total files across 8 parts)")
PYEOF

echo "==> Generator finished successfully."
