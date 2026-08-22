# QEMU source correspondence notice

Serpy downloads QEMU at first use into the current user's managed runtime directory; the portable Serpy ZIP does not embed a QEMU binary.

The Windows runtime lock in `config/versions.yaml` is:

- Product version: QEMU 11.1.0
- Windows installer: `https://qemu.weilnetz.de/w64/qemu-w64-setup-20260811.exe`
- Installer SHA-256: `f98a8aeb5f7faea9765b6dee28316c266cd179d80354a2fed8e50176f9a2e59f`
- Corresponding upstream source: `https://download.qemu.org/qemu-11.1.0.tar.xz`

QEMU is licensed under GPL-2.0-or-later. Source code, license information, and upstream notices are available from the QEMU project: <https://www.qemu.org/>.

For Serpy's CI-built Windows bundle path, `eng/qemu/build-windows.ps1` downloads that exact upstream source release, builds only the x86_64 softmmu target with WHPX, slirp, and GnuTLS enabled, and emits the bundle hash used in the runtime manifest. A release containing that bundle must preserve this notice and publish the exact source URL and bundle SHA-256 alongside its artifact.
