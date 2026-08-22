#!/bin/bash
# Emit the provision-complete sentinel on the serial console.
# The .NET host watches the authenticated serial chardev (KTD9) for this line.
# Called as the last runcmd step in cloud-init user-data.
set -euo pipefail
echo 'SERPY_PROVISION_DONE' > /dev/ttyS0
