#!/bin/bash
# Guest helper: initialize data.img for a new appliance.
# Called via QGA from the .NET host during the Initialize operation.
# Preconditions:
#   - /dev/vdb is the attached sparse RAW data.img
#   - No initialized-marker file exists (host checked before calling)
#   - /data is a mount point (fstab entry must exist)
# Postconditions:
#   - /dev/vdb has an ext4 filesystem labeled SERPY_DATA
#   - /data is mounted
#   - /data/mariadb and /data/frappe/sites exist
#   - MariaDB datadir is /data/mariadb (bind mount active)
#   - One Frappe site created and ERPNext installed
#   - Sentinel marker written to /data/.serpy-initialized
set -euo pipefail

SITE_NAME="${1:-site1.local}"
ADMIN_PASSWORD="${2:-}"  # passed via stdin by QGA, never in cmdline args

DATA_DEV="/dev/vdb"
MOUNT_POINT="/data"
MARKER="$MOUNT_POINT/.serpy-initialized"
FRAPPE_DIR="/home/frappe/frappe-bench"
FRAPPE_USER="frappe"

log() { echo "[init-data] $*"; }

# Refuse re-initialization if marker exists on mounted disk.
if mount | grep -q "$MOUNT_POINT"; then
    if [[ -f "$MARKER" ]]; then
        echo "ERROR: $MARKER already exists. Refusing re-initialization." >&2
        exit 1
    fi
else
    log "Formatting $DATA_DEV as ext4 (label SERPY_DATA)…"
    mkfs.ext4 -L SERPY_DATA "$DATA_DEV"

    log "Mounting $DATA_DEV at $MOUNT_POINT…"
    mount -t ext4 "$DATA_DEV" "$MOUNT_POINT"
fi

# Create persistent directories.
log "Creating /data subdirectories…"
mkdir -p "$MOUNT_POINT/mariadb" "$MOUNT_POINT/frappe/sites"

# Stop MariaDB before moving datadir.
log "Stopping MariaDB…"
systemctl stop mariadb

# Initialize MariaDB datadir on data.img.
log "Initializing MariaDB datadir on /data/mariadb…"
mariadb-install-db --user=mysql --datadir="$MOUNT_POINT/mariadb"

# Activate bind mount: /data/mariadb → /var/lib/mysql.
log "Activating bind mounts…"
mount --bind "$MOUNT_POINT/mariadb" /var/lib/mysql
mount --bind "$MOUNT_POINT/frappe/sites" "$FRAPPE_DIR/sites"

# Start MariaDB with the new datadir.
log "Starting MariaDB…"
systemctl start mariadb

# Create site.
log "Creating Frappe site: $SITE_NAME"
if [[ -n "$ADMIN_PASSWORD" ]]; then
    su - "$FRAPPE_USER" -c "cd $FRAPPE_DIR && bench new-site $SITE_NAME --admin-password '$ADMIN_PASSWORD' --mariadb-root-username root --no-mariadb-socket"
else
    su - "$FRAPPE_USER" -c "cd $FRAPPE_DIR && bench new-site $SITE_NAME --mariadb-root-username root --no-mariadb-socket"
fi

# Install ERPNext on the site.
log "Installing ERPNext on $SITE_NAME…"
su - "$FRAPPE_USER" -c "cd $FRAPPE_DIR && bench --site $SITE_NAME install-app erpnext"

# Set default site.
su - "$FRAPPE_USER" -c "cd $FRAPPE_DIR && bench use $SITE_NAME"

# Regenerate nginx config.
su - "$FRAPPE_USER" -c "cd $FRAPPE_DIR && bench setup nginx"
cp "$FRAPPE_DIR/config/nginx.conf" /etc/nginx/conf.d/frappe.conf
nginx -t && systemctl reload nginx

# Write initialization marker.
log "Writing marker $MARKER…"
echo "serpy-initialized-v1" > "$MARKER"

log "Data initialization complete."
