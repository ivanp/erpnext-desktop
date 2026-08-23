#!/usr/bin/env bash
# Generates a local dev-only RSA keypair for signing the QEMU runtime descriptor
# (IR3/IKTD3/IR7). NEVER used for production. Serpy.Core.csproj's
# LoadLocalDevDescriptorTrustKey MSBuild target auto-picks up
# .local-signing/descriptor-signing-public.b64 for local Debug/test builds only;
# a Release build MUST supply the real Serpy publisher key via
# -p:SerpyDescriptorTrustKey=<base64> and fails if it does not.
#
# Output goes to .local-signing/ (gitignored) — the private key is never committed.
set -euo pipefail

OUT_DIR="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/.local-signing}"
mkdir -p "$OUT_DIR"

PRIVATE_PATH="$OUT_DIR/descriptor-signing-private.pem"
PUBLIC_PATH="$OUT_DIR/descriptor-signing-public.pem"
B64_PATH="$OUT_DIR/descriptor-signing-public.b64"

openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$PRIVATE_PATH"
openssl pkey -in "$PRIVATE_PATH" -pubout -out "$PUBLIC_PATH"

PUBLIC_B64=$(openssl pkey -pubin -in "$PUBLIC_PATH" -outform DER 2>/dev/null | base64 -w0)
printf '%s' "$PUBLIC_B64" > "$B64_PATH"

echo "PrivateKeyPath=$PRIVATE_PATH"
echo "PublicKeyPath=$PUBLIC_PATH"
echo "PublicKeySpkiBase64Path=$B64_PATH (auto-picked up by local Debug/test builds)"
