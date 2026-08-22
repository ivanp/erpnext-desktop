#!/bin/bash
# Guest helper: initialize data.img for a new appliance.
# Called through QGA during InitializeOperation.
#
# Credential safety (R11/U4): the host sends the Administrator password as
# QGA stdin. The fixed Python process below inherits that pipe and passes the
# value directly to Frappe's internal site-creation function in memory. It is
# never an argv value, environment value, or guest file readable by frappe.
#
# Preconditions:
# - /dev/vdb is the attached sparse RAW data.img.
# - The host has validated SITE_NAME as a lowercase FQDN.
# - No initialized marker exists (the host checks before calling).
# Postconditions:
# - /data contains the MariaDB datadir and Frappe sites tree.
# - a new Frappe/ERPNext site has been created and configured.
# - /data/.serpy-initialized exists.
set -euo pipefail

SITE_NAME="${1:?site name is required}"
DATA_DEV="/dev/vdb"
MOUNT_POINT="/data"
MARKER="$MOUNT_POINT/.serpy-initialized"
FRAPPE_DIR="/home/frappe/frappe-bench"
FRAPPE_USER="frappe"

log() { echo "[init-data] $*"; }

# A fixed command string is intentional: the validated site name is supplied
# as $1 to the child shell, never interpolated into its source text.
run_bench() {
    runuser -u "$FRAPPE_USER" -- sh -c \
        'cd /home/frappe/frappe-bench && exec ./env/bin/bench "$@"' \
        serpy-bench "$@"
}

# -- Disk setup -------------------------------------------------------------
if mountpoint -q "$MOUNT_POINT"; then
    if [ -f "$MARKER" ]; then
        echo "ERROR: $MARKER already exists. Refusing re-initialization." >&2
        exit 1
    fi
else
    log "Formatting $DATA_DEV as ext4 (label SERPY_DATA)…"
    mkfs.ext4 -L SERPY_DATA "$DATA_DEV"
    log "Mounting $DATA_DEV at $MOUNT_POINT…"
    mount -t ext4 "$DATA_DEV" "$MOUNT_POINT"
fi

# -- Persistent directories -------------------------------------------------
log "Creating /data subdirectories…"
mkdir -p "$MOUNT_POINT/mariadb" "$MOUNT_POINT/frappe/sites"
# The mounted sites tree must be writable by the unprivileged Frappe process
# before it creates the first site and its site_config.json.
chown -R "$FRAPPE_USER":"$FRAPPE_USER" "$MOUNT_POINT/frappe"

# -- MariaDB ----------------------------------------------------------------
log "Stopping MariaDB…"
systemctl stop mariadb
log "Initializing MariaDB datadir on /data/mariadb…"
mariadb-install-db --user=mysql --datadir="$MOUNT_POINT/mariadb"

# Persist the ext4 data disk and both data-bearing bind mounts before the
# appliance target can start them on a later normal boot. The filesystem label
# avoids depending on VirtIO enumeration order.
for entry in \
    "LABEL=SERPY_DATA /data ext4 defaults 0 2" \
    "/data/mariadb /var/lib/mysql none bind 0 0" \
    "/data/frappe/sites /home/frappe/frappe-bench/sites none bind 0 0"; do
    grep -Fqx "$entry" /etc/fstab || echo "$entry" >> /etc/fstab
done

log "Activating bind mounts…"
mount --bind "$MOUNT_POINT/mariadb" /var/lib/mysql
mount --bind "$MOUNT_POINT/frappe/sites" "$FRAPPE_DIR/sites"
log "Starting MariaDB…"
systemctl start mariadb

# -- Site creation ----------------------------------------------------------
# Frappe v16.31.0 exposes the password only as a Click option. Calling its
# documented internal implementation avoids serialising the secret into argv
# while preserving the production site-creation path. The explicit new-site
# context permits the absent site_config.json; _new_site then retains it.
log "Creating Frappe site: $SITE_NAME"
cd "$FRAPPE_DIR"
runuser -u "$FRAPPE_USER" -- "$FRAPPE_DIR/env/bin/python" -c '
import sys
import frappe
from frappe.installer import _new_site

site = sys.argv[1]
password = sys.stdin.readline().rstrip("\r\n")
if not password:
    raise SystemExit("Admin password is required on standard input")
frappe.init(site, sites_path="/home/frappe/frappe-bench/sites", new_site=True)
_new_site(
    None,
    site,
    db_root_username="root",
    admin_password=password,
)
' "$SITE_NAME"

log "Installing ERPNext on $SITE_NAME…"
run_bench --site "$SITE_NAME" install-app erpnext
log "Setting default site…"
run_bench use "$SITE_NAME"

# -- Production config ------------------------------------------------------
log "Regenerating nginx configuration…"
run_bench setup nginx
cp "$FRAPPE_DIR/config/nginx.conf" /etc/nginx/conf.d/frappe.conf
nginx -t && systemctl reload nginx

# Enable appliance target so services start on next normal boot.
systemctl enable serpy-appliance.target

# -- Marker -----------------------------------------------------------------
log "Writing marker $MARKER…"
echo "serpy-initialized-v1" > "$MARKER"
log "Data initialization complete."
