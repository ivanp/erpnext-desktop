# Serpy Release Process

This document describes the automated release workflow, versioning conventions, repository secrets, and packaging pipeline for Serpy Windows MSI releases.

---

## 1. Release Trigger & Workflow

Serpy uses GitHub Actions (`.github/workflows/release.yml`) running on `windows-latest` to build, package, and publish official Windows installer releases automatically when a version tag is pushed to GitHub.

### Cutting a Release

To trigger a release:

```bash
# Ensure current branch (e.g. main) is clean and pushed
git checkout main
git pull origin main

# Create and push the release tag
git tag v1.0.0
git push origin v1.0.0
```

For a release candidate or prerelease:

```bash
git tag v1.0.0-rc.1
git push origin v1.0.0-rc.1
```

The release workflow uses concurrency protection (`release-${{ github.ref }}`) with `cancel-in-progress: false` to ensure tag builds run to completion without overlapping.

---

## 2. Tag Format & Versioning Rules

The version resolution helper (`eng/installer/resolve-release-version.ps1`) parses git tags and validates them against Windows Installer (`msiexec`) versioning requirements:

- **Format:** `v<major>.<minor>.<build>[-<prerelease>]` or `<major>.<minor>.<build>[-<prerelease>]` (e.g. `v1.0.0`, `v1.2.3`, `v2.0.0-rc.1`).
- **Windows Installer Limits:**
  - `Major` $\le$ 255
  - `Minor` $\le$ 255
  - `Build` $\le$ 65535
- **Prerelease Detection:** Tags with prerelease suffixes (e.g., `-rc.1`, `-beta.2`) are automatically detected and cause the workflow to publish the GitHub Release with the `--prerelease` flag.
- **Installer Filename:** `Serpy-<major>.<minor>.<build>.msi` (e.g. `Serpy-1.0.0.msi`).

---

## 3. Build & Packaging Pipeline

When triggered, the release workflow performs the following automated steps:

1. **Tag & Version Resolution:** `eng/installer/resolve-release-version.ps1` extracts `product_version`, `release_tag`, `is_prerelease`, and `msi_filename`.
2. **Signing Preparation:** `eng/installer/prepare-release-signing.ps1` inspects environment secrets, prepares signing assets in an isolated temporary location, and configures MSBuild trust properties.
3. **Serpy.App Native-AOT Compilation:** Compiles `src/Serpy.App/Serpy.App.csproj` as a self-contained Windows x64 Native-AOT binary with `/p:Version=<product_version>` and embeds all provisioning assets (`config/versions.yaml`, `config/build-inputs.json`, `cloud-init/*`, `guest/*`).
4. **Bootstrapper Compilation:** Compiles `src/Serpy.InstallerBootstrapper/Serpy.InstallerBootstrapper.csproj` as a framework-dependent x64 executable.
5. **.NET 10 Runtime Harvesting:** Downloads the pinned Microsoft .NET 10 runtime zip (`dotnet-runtime-10.0.15-win-x64.zip`) and harvests it into the MSI payload under `C:\Program Files\dotnet` (KD_MSI1) so elevated bootstrapper execution succeeds on clean Windows machines without child Burn/msiexec deadlocks.
6. **WiX v5 Packaging:** Packages the MSI via `eng/installer/build-msi.ps1`.
7. **Checksum Generation:** Generates `dist/SHA256SUMS.txt` with SHA-256 hash in coreutils format.
8. **Workflow Artifact Upload:** Uploads `dist/` as a workflow artifact named `serpy-release-msi` (30-day retention).
9. **GitHub Release Publication:** Uses `gh release create` to publish the release with generated release notes and attached assets (`Serpy-<version>.msi` and `SHA256SUMS.txt`).

---

## 4. Signing Posture & Repository Secrets

Serpy supports end-to-end cryptographic integrity verification:

| Repository Secret / Variable | Purpose | Format |
|---|---|---|
| `DESCRIPTOR_SIGNING_KEY` | RSA private key used to sign the QEMU runtime descriptor (`dist/runtime-descriptor.signed.json`). | PEM format (`-----BEGIN RSA PRIVATE KEY-----` or `-----BEGIN PRIVATE KEY-----`). |
| `DESCRIPTOR_TRUST_KEY` | Matching RSA public key trust anchor embedded into Serpy assemblies at build time via `/p:SerpyDescriptorTrustKey`. | Base64-encoded SubjectPublicKeyInfo (SPKI). |
| `HELPER_PUBLISHER_SUBJECT` | Expected Authenticode subject of `Serpy.InstallerBootstrapper.exe` embedded into Serpy assemblies at build time via `/p:SerpyHelperPublisherSubject`. Can be set as either a repository secret or repository variable. | Plaintext string (e.g. `CN=Serpy Publisher, O=Serpy, C=US`). |
| `SIGNING_CERT_PFX_BASE64` | Authenticode certificate used to digitally sign `Serpy.App.exe`, `Serpy.InstallerBootstrapper.exe`, and the output `.msi`. | Base64 string of `.pfx` file bytes. |
| `SIGNING_CERT_PASSWORD` | Password for decrypting the Authenticode `.pfx` certificate. | Plaintext string secret. |

Both `DESCRIPTOR_TRUST_KEY` and `HELPER_PUBLISHER_SUBJECT` are required to activate `/p:SerpyReleasePublish=true`. If only `DESCRIPTOR_TRUST_KEY` is provided without `HELPER_PUBLISHER_SUBJECT`, the build pipeline logs a warning and omits `SerpyReleasePublish=true` to prevent MSBuild compile-time enforcement failure (`RequireReleaseHelperPublisherSubject`).
### Process-Isolated Secret Handling

Sensitive cryptographic material is handled securely:
- Private keys and certificate bytes are decoded strictly into an isolated temporary directory outside the repository workspace (`$env:RUNNER_TEMP\signing` or `%TEMP%\serpy-signing\<guid>`).
- Temporary private key files are wiped immediately upon descriptor generation.
- Decoded certificates and keys are never copied into `publish/` or `dist/` where WiX or artifact upload could harvest them.

### Graceful Fallback for Dev & Forks

When secrets are not configured (e.g. in development forks or community PR tags):
- The workflow logs an informational notice that signing secrets are absent.
- Packaging falls back cleanly to `-AllowMissingDescriptor`, producing a valid, structurally complete MSI for staging or testing.
- Authenticode signing is skipped cleanly when certificate secrets are absent.

---

## 5. Published Release Assets

Each published GitHub Release provides:

- `Serpy-<version>.msi`: Self-contained Windows Installer package.
- `SHA256SUMS.txt`: Cryptographic checksum file formatted for verification via `sha256sum -c SHA256SUMS.txt` or `Get-FileHash`.

Additionally, the workflow run artifacts retain `serpy-release-msi` for 30 days as a secondary persistence channel.

---

## 6. Local Testing & Verification

Maintainers can test version resolution and signing preparation scripts locally:

```powershell
# Test valid version tag
pwsh -File ./eng/installer/resolve-release-version.ps1 -Tag "v1.0.0"

# Test prerelease tag
pwsh -File ./eng/installer/resolve-release-version.ps1 -Tag "v1.2.3-rc.1"

# Test unsigned fallback mode
pwsh -File ./eng/installer/prepare-release-signing.ps1

# Test signed mode using local dev keys (from .local-signing/)
$env:DESCRIPTOR_SIGNING_KEY = Get-Content .local-signing/descriptor-signing-private.pem -Raw
$env:DESCRIPTOR_TRUST_KEY = (Get-Content .local-signing/descriptor-signing-public.b64 -Raw).Trim()
pwsh -File ./eng/installer/prepare-release-signing.ps1
$env:DESCRIPTOR_SIGNING_KEY = $null
$env:DESCRIPTOR_TRUST_KEY = $null
```
