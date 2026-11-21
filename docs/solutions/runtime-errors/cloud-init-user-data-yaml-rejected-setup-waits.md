---
title: A rejected cloud-init user-data file made setup wait for hours, and the first real build then failed in 14 more places
date: 2026-11-21
category: runtime-errors
module: Serpy.Core, guest provisioning
problem_type: runtime_error
component: build pipeline
severity: critical
symptoms:
  - "Build waited for SERPY_PROVISION_DONE until a two hour timeout, with no log and no failure"
  - "cloud-init printed 'Failed to load config' on the guest console, and nothing read the console"
  - "Every first real build then failed one stage later, behind a long download"
root_cause: missing_validation
resolution_type: code_fix
tags:
  - cloud-init
  - qemu
  - provisioning
  - serial-console
  - qga
  - debian
  - frappe
  - bench
framework_version: dotnet 10.0
---

# A rejected cloud-init user-data file made setup wait for hours

## Problem

The first run of Build on a clean PC never finished. The host started the guest, connected to its serial
console and waited for `SERPY_PROVISION_DONE`. The guest never printed it, because cloud-init had refused to
load the user-data file (a shell line in `runcmd` held `: ` and was read as a YAML mapping). Nothing told the
host, so it waited until its two hour timeout. There was no log file, and the window showed a bar at 30%.

Fixing that revealed the real finding: the provisioning had never run end to end on a real guest. Fourteen
more defects stood behind it, each visible only one stage after the last, and each costing between one and
eight minutes of a build.

## What the build log showed, in the order it showed it

| Attempt | Stage reached | Cause | Fix |
|---|---|---|---|
| 1 | `database` | The base image carries security-updated libraries (`libexpat1`, `libmount1`, `libuuid1`) and the snapshot's plain `trixie` suite only has the older `-dev` packages. The Debian `mariadb-client` is newer than the pinned server and breaks it. | Add the snapshot's `trixie-security` and `trixie-updates` suites. Pin every MariaDB package to `archive.mariadb.org`. |
| 3 | `database` | `mariadb-server`'s postinst tried to start the service. Its drop-in requires `/data`, which does not exist at build time. | `policy-rc.d` denies service starts while the image is built. |
| 4 | `language-tools` | Debian disables `ensurepip` and `pip` for the system Python. | Install `bench` in its own virtual environment. |
| 5 | `erpnext-source` | `bench` runs `uv`, and the `frappe` user's PATH did not have it. | Pin `uv` beside `bench` and link it onto the PATH. |
| 7 | `erpnext-source` | `bench init` writes a backup job through `crontab`, and the image has no `cron`. The error sat behind bench's rollback prompt. | Install `cron`. |
| 8 | after `DONE` | `guest-exec` was sent with `"env": null`, which the guest agent rejects. | Leave unset fields out of QMP and QGA messages. |
| 10 | version gate | The gate compared `Python 3.14.7` with the lock `3.14.7`. | A parser for the banners of the four tools. |
| 11 | `Initialize` | Connected to the agent 10 s after the boot. The 10 s timer's cancel was reported as a user cancel. | Retry until the agent answers; a timer is a timeout. |
| 13 | `init-data.sh` | `mount: /data: mount point does not exist`. | Create `/data` in the image. |
| 15 | `init-data.sh` | The bind mount hides `apps.txt`, `common_site_config.json` and `assets/` behind an empty tree. | Seed them onto the data disk before the bind. |

Other defects found on the way, each with its own test: a guest script that held `/dev/ttyS0` open with `tee`
and lost every line once the host connected; a console that nothing drained; `./env/bin/bench`, which does not
exist; and test classes that wrote a `data.img` into the real appliance folder.

## What Didn't Work

1. **Quoting the one line that broke the YAML.** The template had 31 shell lines in `runcmd`, and any of them
   could carry the same class of error. The script now travels base64 encoded in `write_files` and `runcmd`
   holds one entry, so no shell text passes through the YAML parser.
2. **Reading only the last lines of a failed build.** bench ends a failed `init` with a rollback prompt that
   answers itself, so the last lines said nothing. The first error is further up, and the useful habit was
   reading from the failed marker backwards.
3. **Guessing the next missing tool.** Each guess cost a build. A one-second check of the commands that bench
   calls, run before the long stage, replaced the guessing.
4. **A fixed ten second wait for a booting guest.** The agent comes up some seconds after the boot, so one
   attempt always loses the race. The wait is now a retry bounded by a deadline.

## Solution

1. The guest prints `SERPY_PROVISION_STARTED`, `SERPY_STAGE <id>`, `SERPY_PROVISION_DONE` and
   `SERPY_PROVISION_FAILED <stage> <code>` to the serial console, and each line is written through a fresh open
   of the device.
2. One `ProvisionMonitor` takes a line stream, a clock and a process-exit signal. It fails at once on a failure
   line, on the end of the stream and on QEMU's exit, and with "provisioning did not start" when no started line
   arrives within five minutes. Lines that follow a failure are still read for a short time, because the package
   manager prints its explanation after the failed marker.
3. Every operation writes one durable log that includes the guest's console lines, and the password is masked at
   the point of writing.
4. `ConsolePump` reads the console of any VM that Serpy starts with a serial chardev, because an undrained
   console can block the guest on its own console writes.
5. Every step of a QGA connect (TCP, handshake, sync) is under one time limit, and a failed attempt disposes its
   socket, because QEMU serves one client per chardev.

## Why This Works

The host can only see what the guest tells it. The first version trusted the guest to finish, and a guest that
cannot start its script says nothing. Making the host expect a started line, a stage line every so often and a
done or failed line turns "silent" into a named failure within minutes. The log with the guest's own output is
what made fourteen further defects readable at all: each was one line of apt, bench or systemd output.

## Prevention

- Run the real thing early. The unit tests all passed while the provisioning had never completed once, because
  every test fed the code values that the real tools do not print (`"11.8.3"` for a version, a bare string for a
  mutable field). A test that feeds the measured output of the real tool is cheap once you have it, and each of
  the fixes above has one.
- Make a failure cheap to reproduce. `SERPY_RESUME_FROM_BUILT=1` skips a 12 minute build when the image is
  already accepted, so a failure after the build is iterated on in minutes.
- Check the tools a long step needs before the step starts.
- Treat an operation's own timer as a failure with a cause. Only the caller's token means the user cancelled.
- Tests that touch the real appliance folder wipe a user's data and make other tests depend on it. Every test
  that reads app data uses `SERPY_TEST_APPDATA`.
