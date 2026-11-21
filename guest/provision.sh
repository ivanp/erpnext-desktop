#!/bin/bash
# Serpy guest provisioning. Runs once from cloud-init (a single runcmd entry) when the
# site-less system image is built. It carries every command that used to be a runcmd line,
# so no shell text passes through the YAML parser (KTD1).
#
# Progress and failure go to the serial console, where the host reads them (KTD2):
#   SERPY_PROVISION_STARTED                  first line, before any work
#   SERPY_STAGE <id>                         a stage begins
#   SERPY_PROVISION_DONE                     every stage finished
#   SERPY_PROVISION_FAILED <stage> <code>    a command failed; the script stops
# Command output is copied to the serial console as well, so the host log shows it.
set -uo pipefail

SERIAL=/dev/ttyS0
STAGE_FILE=/run/serpy-provision-stage
CACHE=/srv/cache

# A marker starts on its own line even when a command left a partial line behind.
mark() { printf '\n%s\n' "$*" > "$SERIAL" 2>/dev/null || true; }
stage() { echo "$1" > "$STAGE_FILE"; mark "SERPY_STAGE $1"; }

wait_for_disk() {
  local dev="$1"
  local attempts=0
  while [ ! -b "$dev" ] && [ ! -e "$dev" ]; do
    attempts=$((attempts + 1))
    if [ "$attempts" -ge 60 ]; then
      echo "Timed out waiting for block device: $dev"
      udevadm settle 2>/dev/null || true
      ls -l /dev/disk/by-id/ 2>/dev/null || true
      return 1
    fi
    udevadm trigger --subsystem-match=block 2>/dev/null || true
    udevadm settle 2>/dev/null || true
    sleep 0.5
  done
}

materialize_git_repo() {
  local name="$1"
  local branch="$2"
  local commit_ref="$3"
  local target="/srv/cache/git/${name}.git"
  local archive="/srv/cache/git/${name}.tar.gz"

  if [ -d "$target" ] && [ -e "$target/HEAD" ]; then
    return 0
  fi

  if [ ! -f "$archive" ]; then
    echo "Archive not found for ${name} under /srv/cache/git"
    return 1
  fi

  local actual_commit
  actual_commit=$( { gzip -dc "$archive" 2>/dev/null || true; } | git get-tar-commit-id )
  if [ -z "$actual_commit" ]; then
    echo "git get-tar-commit-id produced no commit id for ${archive}"
    return 1
  fi

  if [ "$actual_commit" != "$commit_ref" ]; then
    echo "Commit id mismatch for ${name}: expected ${commit_ref}, got ${actual_commit}"
    return 1
  fi

  local tmp_dir
  tmp_dir=$(mktemp -d "/tmp/git-${name}-XXXXXX")
  tar -C "$tmp_dir" -xf "$archive"

  local root_dir
  root_dir=$(find "$tmp_dir" -mindepth 1 -maxdepth 1 -type d -print -quit)
  if [ -z "$root_dir" ]; then
    root_dir="$tmp_dir"
  fi
  if [ -f "$root_dir/banking/yarn.lock" ]; then
    cat >> "$root_dir/banking/yarn.lock" <<'EOF'

follow-redirects@^1.16.0:
  version "1.16.3"
  resolved "https://registry.yarnpkg.com/follow-redirects/-/follow-redirects-1.16.3.tgz#62b60932427f616327014a5e401e8fc5e9027e9b"
  integrity sha512-toOSoqN6d51Wjf1aOFJYcoVv1aND9rj/CwjLU0Vz40oDuSpWDayCeKe4JpL5NL0WdFvoOOCI3BRo69HahjbR1A==

proxy-from-env@^2.1.0:
  version "2.1.0"
  resolved "https://registry.yarnpkg.com/proxy-from-env/-/proxy-from-env-2.1.0.tgz#a7487568adad577cfaaa7e88c49cab3ab3081aba"
  integrity sha512-cJ+oHTW1VAEa8cJslgmUZrc+sjRKgAKl3Zyse6+PV38hZe/V6Z14TbCuXcan9F9ghlz4QrFr2c92TNF82UkYHA==
EOF
  fi
  local src_dir="/srv/cache/git/${name}"
  rm -rf "$src_dir"
  mkdir -p "$src_dir"
  cp -a "$root_dir"/* "$src_dir/"

  mkdir -p "$target"
  (
    cd "$root_dir"
    git init -q
    git config user.email "serpy@local"
    git config user.name "Serpy"
    git add -A
    git commit -q -m "pinned ${name} ${branch}"
    git branch -M pinned
    git tag "$branch"
    if [ -n "$commit_ref" ] && [ "$commit_ref" != "$branch" ]; then
      git branch "$commit_ref"
      git tag "$commit_ref"
    fi
    git clone --bare . "$target" -q
  )
  rm -rf "$tmp_dir"
}

unpack_git_sources() {
  mkdir -p /srv/cache/git
  materialize_git_repo "frappe" "v16.31.0" "6a329d068416768ec47ccd3326b9cc95a8d7bf99"
  materialize_git_repo "erpnext" "v16.32.3" "11e0ba0a1c45f217e2e73e885f699102d06da325"
  materialize_git_repo "pypika" "v0.48.9" "2c50e6142b2d61d2d243e466fdd5dc03b3d918f2"
  materialize_git_repo "gunicorn" "23.0.0" "54b59ca884c79dcf04d4882658a32bbaf17e999f"
  materialize_git_repo "air-datepicker" "master" "ed37b94d95c68d8544357e330be0c89d044a3eea"
  chmod -R a+rwX /srv/cache/git

  # Write Python overrides for git-pinned packages so pip and uv install from local source trees
  printf '%s\n' \
    'PyPika @ file:///srv/cache/git/pypika' \
    'gunicorn @ file:///srv/cache/git/gunicorn' \
    > /etc/serpy-python-overrides.txt
  chmod 0644 /etc/serpy-python-overrides.txt
}

main() {
  set -e
  # Debconf must never prompt: a package postinst waiting on a terminal that does not exist hangs the build.
  export DEBIAN_FRONTEND=noninteractive
  # No package may start a service while the image is built. mariadb and supervisor require the
  # data disk (their serpy-data drop-ins), which does not exist yet, so a start would fail and
  # abort the install. Policy 101 means "not allowed" and invoke-rc.d then reports success.
  printf '%s\n' '#!/bin/sh' 'exit 101' > /usr/sbin/policy-rc.d
  chmod 0755 /usr/sbin/policy-rc.d

  stage os-packages
  # Upstream snapshot sources from which the offline cache was collected:
  # snapshot.debian.org/archive/debian/20260822T000000Z/ trixie main
  # snapshot.debian.org/archive/debian/20260822T000000Z/ sid main
  wait_for_disk /dev/disk/by-id/virtio-serpycache
  wait_for_disk /dev/disk/by-id/virtio-serpyscratch

  mkfs.ext4 -F -q /dev/disk/by-id/virtio-serpyscratch
  mkdir -p /srv
  mount /dev/disk/by-id/virtio-serpyscratch /srv
  mkdir -p "$CACHE"
  tar -C "$CACHE" -xf /dev/disk/by-id/virtio-serpycache


  cd "$CACHE/debs"
  apt-ftparchive packages . > Packages
  gzip -kf -9 Packages
  apt-ftparchive release . > Release

  rm -f /etc/apt/sources.list /etc/apt/sources.list.d/*.list /etc/apt/sources.list.d/*.sources /etc/apt/preferences.d/*
  printf '%s\n' 'deb [trusted=yes] file:/srv/cache/debs ./' > /etc/apt/sources.list.d/serpy-offline.list
  printf '%s\n' 'Acquire::Check-Valid-Until "false";' > /etc/apt/apt.conf.d/99serpy-snapshot

  printf '%s\n' \
    'PIP_NO_INDEX=1' \
    'PIP_FIND_LINKS=/srv/cache/wheels' \
    'PIP_NO_CACHE_DIR=1' \
    'PIP_CONSTRAINT=/etc/serpy-python-overrides.txt' \
    'UV_NO_INDEX=1' \
    'UV_FIND_LINKS=/srv/cache/wheels' \
    'UV_OFFLINE=1' \
    'UV_OVERRIDE=/etc/serpy-python-overrides.txt' \
    > /etc/serpy-offline.env

  printf '%s\n' \
    'export PIP_NO_INDEX=1' \
    'export PIP_FIND_LINKS=/srv/cache/wheels' \
    'export PIP_NO_CACHE_DIR=1' \
    'export UV_NO_INDEX=1' \
    'export PIP_CONSTRAINT=/etc/serpy-python-overrides.txt' \
    'export UV_NO_INDEX=1' \
    'export UV_FIND_LINKS=/srv/cache/wheels' \
    'export UV_OFFLINE=1' \
    'export UV_OVERRIDE=/etc/serpy-python-overrides.txt' \
    > /etc/profile.d/serpy-offline.sh
  chmod 0644 /etc/serpy-offline.env /etc/profile.d/serpy-offline.sh

  printf '[url "file:///srv/cache/git/pypika.git"]\n	insteadOf = https://github.com/frappe/pypika.git\n	insteadOf = https://github.com/frappe/pypika\n' > /etc/gitconfig
  printf '[url "file:///srv/cache/git/gunicorn.git"]\n	insteadOf = https://github.com/frappe/gunicorn.git\n	insteadOf = https://github.com/frappe/gunicorn\n' >> /etc/gitconfig
  printf '[url "file:///srv/cache/git/frappe.git"]\n	insteadOf = https://github.com/frappe/frappe.git\n	insteadOf = https://github.com/frappe/frappe\n' >> /etc/gitconfig
  printf '[url "file:///srv/cache/git/erpnext.git"]\n	insteadOf = https://github.com/frappe/erpnext.git\n	insteadOf = https://github.com/frappe/erpnext\n' >> /etc/gitconfig
  printf '[url "file:///srv/cache/git/air-datepicker.git"]\n	insteadOf = https://github.com/frappe/air-datepicker.git\n	insteadOf = https://github.com/frappe/air-datepicker\n' >> /etc/gitconfig

  apt-get update -q
  apt-get install -y curl git
  # The synthesized repositories belong to root, and git refuses to read a repository owned by another
  # user ("dubious ownership"). The build user is the frappe user, so these four read-only paths are
  # trusted explicitly; no wildcard.
  for repo in frappe erpnext pypika gunicorn air-datepicker; do
    git config --system --add safe.directory "/srv/cache/git/${repo}.git"
    git config --system --add safe.directory "/srv/cache/git/${repo}"
  done
  echo "=== PROVING RESTRICTED NETWORK (restrict=on) ==="
  if curl -m 5 -sS https://deb.debian.org >/dev/null 2>&1; then
    echo "FATAL: Outbound curl succeeded! Guest network is NOT restricted!"
    exit 99
  else
    echo "CONFIRMED: curl -m 5 https://deb.debian.org failed (exit code $?) as expected under restrict=on"
  fi
  if getent hosts deb.debian.org >/dev/null 2>&1; then
    echo "FATAL: DNS resolution succeeded! Guest network is NOT restricted!"
    exit 99
  else
    echo "CONFIRMED: getent hosts deb.debian.org failed (exit code $?) as expected under restrict=on"
  fi

  unpack_git_sources

  stage database
  # MariaDB 11.8.3 upstream repository origin and signing key:
  # archive.mariadb.org/mariadb-11.8.3
  # mariadb-keyring-2025.gpg
  # All packages resolve offline from the flat file repository (/srv/cache/debs).
  # cron supplies crontab (bench init writes its backup schedule through it) and fontconfig supplies
  # fc-cache (bench init refreshes the font cache). pkgconf, which gives pkg-config, arrives with the
  # -dev packages.
  # libmariadb-dev is the client library that the Python mysqlclient module is built against. The
  # Oracle libmysqlclient-dev that Debian offers instead builds a module that asks the server for the
  # mysql_native_password plugin from a plugin directory that package does not ship, so every login
  # fails ("Authentication plugin 'mysql_native_password' cannot be loaded"). It also conflicts with
  # libmariadb-dev, so it must not be requested.
  apt-get install -y git cron fontconfig qemu-guest-agent nginx supervisor python3-pip build-essential libssl-dev libffi-dev libmariadb-dev libmariadb-dev-compat libxml2-dev libxslt-dev libjpeg-dev zlib1g-dev libcairo2-dev redis-server=5:8.0.2-3+deb13u2 mariadb-server=1:11.8.3+maria~deb13

  stage language-tools
  apt-get install -y python3.14=3.14.7-1 python3.14-dev=3.14.7-1 python3.14-venv=3.14.7-1
  # Debian disables ensurepip and pip for the system Python, so the bench command lives in its
  # own virtual environment and is linked onto the PATH for every user.
  python3.14 -m venv /opt/serpy-tools
  # uv is pinned beside bench: bench 5.31.0 accepts any 0.11.x and would otherwise take the newest.
  /opt/serpy-tools/bin/pip install --no-index --find-links /srv/cache/wheels --no-cache-dir frappe-bench==5.31.0 uv==0.11.33
  # bench runs uv to create the bench environment, and it finds uv on the PATH of the frappe user.
  ln -sf /opt/serpy-tools/bin/bench /usr/local/bin/bench
  ln -sf /opt/serpy-tools/bin/uv /usr/local/bin/uv
  # Node's official archive is pinned by both versioned filename and published SHA-256.
  cp /srv/cache/tools/node.tar.xz /tmp/node-v24.2.0-linux-x64.tar.xz
  echo '91a0794f4dbc94bc4a9296139ed9101de21234982bae2b325e37ebd3462273e5  /tmp/node-v24.2.0-linux-x64.tar.xz' | sha256sum -c -
  tar -C /usr/local --strip-components=1 -xJf /tmp/node-v24.2.0-linux-x64.tar.xz
  rm -f /tmp/node-v24.2.0-linux-x64.tar.xz
  # bench init and bench build run yarn for every app and stop when it is missing. Node's archive
  # does not carry it, so it is installed from the cached npm package.
  npm install -g /srv/cache/tools/yarn-1.22.22.tgz
  yarn --version

  # Every command that bench, yarn and the asset build call must exist before the long steps run.
  # A missing one otherwise surfaces about nine minutes in, behind bench's rollback prompt.
  for tool in git crontab yarn uv node npm redis-server mariadbd nginx supervisord pkg-config fc-cache; do
    command -v "$tool" > /dev/null || { echo "missing tool: $tool"; exit 127; }
  done

  stage erpnext-source
  useradd -m -s /bin/bash frappe
  install -d -m 0755 -o frappe -g frappe /home/frappe/.cache /home/frappe/.config /home/frappe/.local /home/frappe/.npm
  printf 'yarn-offline-mirror "/srv/cache/yarn-mirror"\n--install.offline true\n--install.frozen-lockfile true\n' > /home/frappe/.yarnrc
  chown frappe:frappe /home/frappe/.yarnrc
  chown -R frappe:frappe "$CACHE/yarn-mirror" "$CACHE/git" 2>/dev/null || true
  chmod -R a+rwX "$CACHE/git" 2>/dev/null || true

  # bench parses the remote as https://<host>/<org>/<repo> to name the app, so it is given the real
  # GitHub URLs; the insteadOf rewrites in /etc/gitconfig send every git call to the local cache.
  su - frappe -c "set -a; . /etc/serpy-offline.env; set +a; bench init --frappe-path https://github.com/frappe/frappe --frappe-branch v16.31.0 --python python3.14 /home/frappe/frappe-bench"
  su - frappe -c "set -a; . /etc/serpy-offline.env; set +a; cd /home/frappe/frappe-bench && bench get-app erpnext https://github.com/frappe/erpnext --branch v16.32.3"

  stage erpnext-assets
  su - frappe -c "set -a; . /etc/serpy-offline.env; set +a; cd /home/frappe/frappe-bench && bench build --production"

  stage services
  su - frappe -c "set -a; . /etc/serpy-offline.env; set +a; cd /home/frappe/frappe-bench && bench setup nginx"
  cp /home/frappe/frappe-bench/config/nginx.conf /etc/nginx/conf.d/frappe.conf
  nginx -t
  su - frappe -c "set -a; . /etc/serpy-offline.env; set +a; cd /home/frappe/frappe-bench && bench setup supervisor"
  cp /home/frappe/frappe-bench/config/supervisor.conf /etc/supervisor/conf.d/frappe.conf

  # The mount point that init-data.sh and every later boot mount onto. It belongs to the system
  # image, because the data disk holds only data: nothing creates /data at first mount, and the
  # first real Initialize failed with "mount point does not exist".
  install -d -m 0755 /data
  systemctl daemon-reload
  systemctl disable mariadb.service supervisor.service

  # Remove temporary build/offline artifacts and unmount scratch disk so the
  # runtime system.qcow2 has zero dependency on /srv or temporary scratch disks.
  rm -f /etc/serpy-offline.env /etc/profile.d/serpy-offline.sh /etc/gitconfig /home/frappe/.yarnrc /etc/serpy-python-overrides.txt
  rm -f /etc/apt/sources.list.d/serpy-offline.list /etc/apt/apt.conf.d/99serpy-snapshot
  sed -i '\|/srv|d' /etc/fstab 2>/dev/null || true
  umount -f /srv 2>/dev/null || true

  # The guest agent was installed while starts were denied, and the host needs it for the
  # version check that follows. It is the only service that has to run now.
  rm -f /usr/sbin/policy-rc.d
  systemctl start qemu-guest-agent
}

# Each line goes to the serial device through a fresh open. A handle that stays open (tee did)
# gets "Input/output error" once the host connects its console, and every later line is lost,
# which hides the very error the host needs. The same lines go to a file inside the guest.
serial_line() {
  printf '%s\n' "$1" >> /var/log/serpy-provision.log 2>/dev/null || true
  printf '%s\n' "$1" > "$SERIAL" 2>/dev/null || true
}

mark SERPY_PROVISION_STARTED
echo unknown > "$STAGE_FILE"

main 2>&1 | while IFS= read -r line || [ -n "$line" ]; do serial_line "$line"; done
rc=${PIPESTATUS[0]}

if [ "$rc" -ne 0 ]; then
  mark "SERPY_PROVISION_FAILED $(cat "$STAGE_FILE" 2>/dev/null || echo unknown) $rc"
  exit "$rc"
fi

# The host performs the post-sentinel version gate and the QMP powerdown.
mark SERPY_PROVISION_DONE
