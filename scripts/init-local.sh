#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
umask 077
command -v openssl >/dev/null || { echo 'OpenSSL is required.' >&2; exit 1; }
if [[ -e .env || -e secrets/s3.json || -e secrets/data-protection.pfx ]]; then
  echo 'Local configuration already exists; refusing to overwrite credentials or the Data Protection key.' >&2
  exit 1
fi
mkdir -p secrets backups
chmod 700 secrets
fb_db_secret=$(openssl rand -hex 32)
fb_s3_access=$(openssl rand -hex 16)
fb_s3_secret=$(openssl rand -hex 32)
fb_dp_password=$(openssl rand -hex 32)
fb_bootstrap_password="Aa1!$(openssl rand -hex 18)"
fb_release=$(git rev-parse --short=12 HEAD 2>/dev/null || echo local)
cat > .env <<EOF
POSTGRES_PASSWORD=$fb_db_secret
S3_ACCESS_KEY=$fb_s3_access
S3_SECRET_KEY=$fb_s3_secret
DATA_PROTECTION_PASSWORD=$fb_dp_password
FIRSTBLICK_RELEASE=$fb_release
API_PORT=8080
POSTGRES_PORT=5432
S3_PORT=8333
LOCAL_UID=$(id -u)
LOCAL_GID=$(id -g)
Bootstrap__Email=owner@example.invalid
Bootstrap__Password=$fb_bootstrap_password
Bootstrap__TenantName='Mein Dachdeckerbetrieb'
OPERATIONS_OPERATOR_EMAIL=recovery-operator@example.invalid
OPERATIONS_OPERATOR_PASSWORD=Aa1!$(openssl rand -hex 24)
EOF
cat > secrets/s3.json <<EOF
{"identities":[{"name":"firstblick-local","credentials":[{"accessKey":"$fb_s3_access","secretKey":"$fb_s3_secret"}],"actions":["Admin","Read","Write","List","Tagging"]}]}
EOF
fb_key=$(mktemp secrets/.dp-key.XXXXXX)
fb_cert=$(mktemp secrets/.dp-cert.XXXXXX)
trap 'rm -f -- "$fb_key" "$fb_cert"' EXIT
openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 -nodes -subj '/CN=FirstBlick local Data Protection' -keyout "$fb_key" -out "$fb_cert" >/dev/null 2>&1
export FIRSTBLICK_PFX_PASSWORD="$fb_dp_password"
openssl pkcs12 -export -inkey "$fb_key" -in "$fb_cert" -out secrets/data-protection.pfx -passout env:FIRSTBLICK_PFX_PASSWORD
unset FIRSTBLICK_PFX_PASSWORD
# Bind mounts retain file modes: the app UID 1654 and Seaweed UID 1000 must read
# these files. The host secrets directory stays 0700; .env stays 0600.
chmod 644 secrets/data-protection.pfx secrets/s3.json
echo 'Local configuration created. Edit Bootstrap__Email and Bootstrap__TenantName in .env, then run scripts/start-local.sh.'
