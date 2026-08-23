# QEMU source correspondence notice

Serpy downloads QEMU at first use into the current user's managed runtime directory; the portable Serpy ZIP does not embed a QEMU binary.

The Windows runtime descriptor in `config/versions.yaml` records:

- Product version: QEMU 11.1.0
- Immutable Windows archive URL and SHA-256: populated together by the release process
- Corresponding upstream source: `https://download.qemu.org/qemu-11.1.0.tar.xz`

QEMU is licensed under GPL-2.0-or-later. Source code, license information, and upstream notices are available from the QEMU project: <https://www.qemu.org/>.

For Serpy's CI-built Windows bundle path, `eng/qemu/build-windows.ps1` downloads that exact upstream source release, builds only the x86_64 softmmu target with WHPX, slirp, and GnuTLS enabled, and emits the archive hash used in the runtime descriptor. A release containing that archive must preserve this notice and publish the exact source URL, immutable archive URL, and SHA-256 alongside its artifact.
