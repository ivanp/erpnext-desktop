#!/bin/bash
# Serpy guest provisioning. Runs once from cloud-init (a single runcmd entry) when the
# system image is built. It carries every command that used to be a runcmd line, so no
# shell text passes through the YAML parser (KTD1).
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

# A marker starts on its own line even when a command left a partial line behind.
mark() { printf '\n%s\n' "$*" > "$SERIAL" 2>/dev/null || true; }
stage() { echo "$1" > "$STAGE_FILE"; mark "SERPY_STAGE $1"; }

main() {
  set -e
  export DEBIAN_FRONTEND=noninteractive

  # No package may start a service while the image is built. mariadb and supervisor require the
  # data disk (their serpy-data drop-ins), which does not exist yet, so a start would fail and
  # abort the install. Policy 101 means "not allowed" and invoke-rc.d then reports success.
  printf '%s\n' '#!/bin/sh' 'exit 101' > /usr/sbin/policy-rc.d
  chmod 0755 /usr/sbin/policy-rc.d

  stage os-packages
  # All Debian packages resolve from this immutable archive snapshot. Trixie
  # supplies Redis 8.0.2; the manifest records that exact available version.
  rm -f /etc/apt/sources.list /etc/apt/sources.list.d/*.list /etc/apt/sources.list.d/*.sources
  printf '%s\n' 'deb [check-valid-until=no] https://snapshot.debian.org/archive/debian/20260822T000000Z/ trixie main' > /etc/apt/sources.list.d/serpy-trixie.list
  printf '%s\n' 'Acquire::Check-Valid-Until "false";' > /etc/apt/apt.conf.d/99serpy-snapshot
  # Python 3.14 is isolated to the same immutable sid snapshot; all non-Python
  # packages retain the trixie candidate priority.
  printf '%s\n' 'deb [check-valid-until=no] https://snapshot.debian.org/archive/debian/20260822T000000Z/ sid main' > /etc/apt/sources.list.d/serpy-python314.list
  printf '%s\n' 'Package: *' 'Pin: release a=unstable' 'Pin-Priority: 1' '' 'Package: python3.14 python3.14-dev python3.14-venv libpython3.14 libpython3.14-minimal libpython3.14-stdlib libpython3.14-dev' 'Pin: release a=unstable' 'Pin-Priority: 1001' > /etc/apt/preferences.d/serpy-python314
  # The base image already carries the security-updated runtime libraries, so the matching -dev
  # packages must come from the same snapshot's security and updates suites.
  printf '%s\n' 'deb [check-valid-until=no] https://snapshot.debian.org/archive/debian-security/20260822T000000Z/ trixie-security main' > /etc/apt/sources.list.d/serpy-trixie-security.list
  printf '%s\n' 'deb [check-valid-until=no] https://snapshot.debian.org/archive/debian/20260822T000000Z/ trixie-updates main' > /etc/apt/sources.list.d/serpy-trixie-updates.list
  apt-get update -q
  apt-get install -y curl

  stage database
  # MariaDB is pinned to its immutable minor-series archive rather than the
  # mutable 11.8 repository endpoint.
  install -d -m 0755 /etc/apt/keyrings
  curl -fsSL https://supplychain.mariadb.com/mariadb-keyring-2025.gpg -o /etc/apt/keyrings/mariadb-keyring.gpg
  printf '%s\n' 'deb [signed-by=/etc/apt/keyrings/mariadb-keyring.gpg] https://archive.mariadb.org/mariadb-11.8.3/repo/debian trixie main' > /etc/apt/sources.list.d/serpy-mariadb.list
  # Debian ships a newer MariaDB client than the pinned 11.8.3 server and would win on version
  # number, and that client breaks the pinned server. Every MariaDB package comes from its own repo.
  printf '%s\n' 'Package: mariadb-* libmariadb* libmariadbd* galera-4' 'Pin: origin archive.mariadb.org' 'Pin-Priority: 1001' > /etc/apt/preferences.d/serpy-mariadb
  apt-get update -q
  # One transaction keeps the MariaDB client libraries and the server on one source.
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
  apt-get install -y -t sid python3.14=3.14.7-1 python3.14-dev=3.14.7-1 python3.14-venv=3.14.7-1
  # Debian disables ensurepip and pip for the system Python, so the bench command lives in its
  # own virtual environment and is linked onto the PATH for every user.
  python3.14 -m venv /opt/serpy-tools
  # uv is pinned beside bench: bench 5.31.0 accepts any 0.11.x and would otherwise take the newest.
  /opt/serpy-tools/bin/pip install --no-cache-dir frappe-bench==5.31.0 uv==0.11.33
  # bench runs uv to create the bench environment, and it finds uv on the PATH of the frappe user.
  ln -sf /opt/serpy-tools/bin/bench /usr/local/bin/bench
  ln -sf /opt/serpy-tools/bin/uv /usr/local/bin/uv
  # Node's official archive is pinned by both versioned URI and published SHA-256.
  curl -fsSLo /tmp/node.tar.xz https://nodejs.org/dist/v24.2.0/node-v24.2.0-linux-x64.tar.xz
  echo '91a0794f4dbc94bc4a9296139ed9101de21234982bae2b325e37ebd3462273e5  /tmp/node.tar.xz' | sha256sum -c -
  tar -C /usr/local --strip-components=1 -xJf /tmp/node.tar.xz
  rm -f /tmp/node.tar.xz
  # bench init and bench build run yarn for every app and stop when it is missing. Node's archive
  # does not carry it, so it is installed from the npm registry at an exact version.
  npm install -g yarn@1.22.22
  yarn --version

  # Every command that bench, yarn and the asset build call must exist before the long steps run.
  # A missing one otherwise surfaces about nine minutes in, behind bench's rollback prompt.
  for tool in git crontab yarn uv node npm redis-server mariadbd nginx supervisord pkg-config fc-cache; do
    command -v "$tool" > /dev/null || { echo "missing tool: $tool"; exit 127; }
  done

  stage erpnext-source
  useradd -m -s /bin/bash frappe
  su - frappe -c "bench init --frappe-branch v16.31.0 --python python3.14 /home/frappe/frappe-bench && cd /home/frappe/frappe-bench/apps/frappe && git checkout --detach 6a329d068416768ec47ccd3326b9cc95a8d7bf99 && git rev-parse HEAD | grep -Fx 6a329d068416768ec47ccd3326b9cc95a8d7bf99"
  su - frappe -c "cd /home/frappe/frappe-bench && bench get-app erpnext --branch v16.32.3 && cd apps/erpnext && git checkout --detach 11e0ba0a1c45f217e2e73e885f699102d06da325 && git rev-parse HEAD | grep -Fx 11e0ba0a1c45f217e2e73e885f699102d06da325"

  stage erpnext-assets
  su - frappe -c "cd /home/frappe/frappe-bench && bench build --production"

  stage services
  su - frappe -c "cd /home/frappe/frappe-bench && bench setup nginx"
  cp /home/frappe/frappe-bench/config/nginx.conf /etc/nginx/conf.d/frappe.conf
  nginx -t
  su - frappe -c "cd /home/frappe/frappe-bench && bench setup supervisor"
  cp /home/frappe/frappe-bench/config/supervisor.conf /etc/supervisor/conf.d/frappe.conf
  # supervisor does not run while the image is built (it needs the data disk), so there is nothing
  # to reread. It reads conf.d/frappe.conf when it first starts on the initialized appliance.

  # The mount point that init-data.sh and every later boot mount onto. It belongs to the system
  # image, because the data disk holds only data: nothing creates /data at first mount, and the
  # first real Initialize failed with "mount point does not exist".
  install -d -m 0755 /data
  systemctl daemon-reload
  systemctl disable mariadb.service supervisor.service

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
