#!/bin/bash
# Guest helper: recover after system.qcow2 replacement.
# Called via QGA after the new system disk is booted with the existing data.img attached.
# Reads version markers from /data, runs mariadb-upgrade if the major increased,
# runs bench migrate, then signals completion.
# Downgrade validation is done by the .NET host before booting (KD5).
set -euo pipefail

DATA_DEV="/dev/vdb"
MOUNT_POINT="/data"
FRAPPE_DIR="/home/frappe/frappe-bench"
FRAPPE_USER="frappe"
SITE_NAME="${1:-site1.local}"

log() { echo "[recover] $*"; }

# Mount /data.
if ! mount | grep -q "$MOUNT_POINT"; then
    log "Mounting $DATA_DEV at $MOUNT_POINT…"
    mount -t ext4 "$DATA_DEV" "$MOUNT_POINT"
fi

# Activate bind mounts.
log "Activating bind mounts…"
mount --bind "$MOUNT_POINT/mariadb" /var/lib/mysql
mount --bind "$MOUNT_POINT/frappe/sites" "$FRAPPE_DIR/sites"

# Start MariaDB.
systemctl start mariadb

# Run mariadb-upgrade (idempotent; required when the MariaDB major increased).
log "Running mariadb-upgrade…"
mariadb-upgrade --user=root

# Run bench migrate.
log "Running bench migrate on $SITE_NAME…"
su - "$FRAPPE_USER" -c "cd $FRAPPE_DIR && bench --site $SITE_NAME migrate"

# Regenerate nginx config for the new system.
su - "$FRAPPE_USER" -c "cd $FRAPPE_DIR && bench setup nginx"
cp "$FRAPPE_DIR/config/nginx.conf" /etc/nginx/conf.d/frappe.conf
nginx -t && systemctl reload nginx

log "Recovery complete."
