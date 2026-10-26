#!/usr/bin/env bats
# SPDX-License-Identifier: GPL-3.0-or-later
# Unit tests for install.sh: argument parsing, help text, output rules, and the install
# steps that can run without Docker or root. The end-to-end install runs in CI separately.

INSTALLER="$BATS_TEST_DIRNAME/../install.sh"

setup() {
  TEST_DIR="$(mktemp -d)"
  export ROLLCALL_DIR="$TEST_DIR/rollcall"
  export ROLLCALL_LOG="$TEST_DIR/install.log"
  mkdir -p "$ROLLCALL_DIR"
}

teardown() {
  rm -rf "$TEST_DIR"
}

# Loads the installer's functions without running main.
load_installer() {
  # shellcheck source=../install.sh
  ROLLCALL_SOURCED=1 source "$INSTALLER"
  set +eu
  setup_output
}

os_release() {
  printf 'ID=%s\nVERSION_ID="%s"\nPRETTY_NAME="%s"\n' "$1" "$2" "$3" >"$TEST_DIR/os-release"
  OS_RELEASE_FILE="$TEST_DIR/os-release"
}

# --- Help and arguments -----------------------------------------------------------------------

@test "help lists every command" {
  run "$INSTALLER" help
  [ "$status" -eq 0 ]
  for command in install update backup restore status logs admin-reset uninstall; do
    [[ "$output" == *"  $command"* ]]
  done
}

@test "--help with no command shows the main help" {
  run "$INSTALLER" --help
  [ "$status" -eq 0 ]
  [[ "$output" == *"Usage: install.sh [command] [options]"* ]]
}

@test "every command has its own help" {
  for command in install update backup restore status logs admin-reset uninstall; do
    run "$INSTALLER" "$command" --help
    [ "$status" -eq 0 ]
    [[ "$output" == "Usage: install.sh $command"* ]]

    run "$INSTALLER" help "$command"
    [ "$status" -eq 0 ]
    [[ "$output" == "Usage: install.sh $command"* ]]
  done
}

@test "install help documents every option" {
  run "$INSTALLER" install --help
  for option in --hostname --tls --email --cert --key --yes letsencrypt custom internal; do
    [[ "$output" == *"$option"* ]]
  done
}

@test "--version prints the installer version" {
  run "$INSTALLER" --version
  [ "$status" -eq 0 ]
  [[ "$output" == "Rollcall installer "* ]]
}

@test "an unknown command fails with exit code 2 and points to help" {
  run "$INSTALLER" frobnicate
  [ "$status" -eq 2 ]
  [[ "$output" == *'Unknown command "frobnicate".'* ]]
  [[ "$output" == *'run "install.sh help"'* ]]
}

@test "an unknown option fails with exit code 2 and points to the command's help" {
  run "$INSTALLER" install --hostnme rc.test
  [ "$status" -eq 2 ]
  [[ "$output" == *"Unknown option --hostnme for install."* ]]
  [[ "$output" == *'run "install.sh install --help"'* ]]
}

@test "an option from another command is rejected" {
  run "$INSTALLER" backup --purge
  [ "$status" -eq 2 ]
  [[ "$output" == *"Unknown option --purge for backup."* ]]
}

@test "an option without its value fails" {
  run "$INSTALLER" install --hostname
  [ "$status" -eq 2 ]
  [[ "$output" == *"Option --hostname needs a value."* ]]
}

@test "an unknown TLS mode fails" {
  run "$INSTALLER" install --tls selfsigned
  [ "$status" -eq 2 ]
  [[ "$output" == *'Unknown TLS mode "selfsigned".'* ]]
}

@test "install options are parsed in both --name value and --name=value form" {
  load_installer
  parse_args --hostname rc.test --tls=custom --email ops@example.com --cert=/c.pem --key /k.pem -y
  [ "$COMMAND" = install ]
  [ "$OPT_HOSTNAME" = rc.test ]
  [ "$OPT_TLS" = custom ]
  [ "$OPT_EMAIL" = ops@example.com ]
  [ "$OPT_CERT" = /c.pem ]
  [ "$OPT_KEY" = /k.pem ]
  [ "$OPT_YES" -eq 1 ]
}

@test "uninstall, backup, and logs options are parsed" {
  load_installer
  parse_args uninstall --purge --yes
  [ "$COMMAND" = uninstall ] && [ "$OPT_PURGE" -eq 1 ] && [ "$OPT_YES" -eq 1 ]

  load_installer
  parse_args backup --output /srv/backups
  [ "$OPT_OUTPUT" = /srv/backups ]

  load_installer
  parse_args logs worker -f
  [ "$ARG" = worker ] && [ "$OPT_FOLLOW" -eq 1 ]
}

@test "restore and admin-reset need exactly one argument" {
  run "$INSTALLER" restore
  [ "$status" -eq 2 ]
  [[ "$output" == *"restore needs exactly one argument: an archive path."* ]]

  run "$INSTALLER" admin-reset a@example.com b@example.com
  [ "$status" -eq 2 ]
  [[ "$output" == *"admin-reset needs exactly one argument: a UPN."* ]]
}

@test "logs rejects an unknown service" {
  run "$INSTALLER" logs nginx
  [ "$status" -eq 2 ]
  [[ "$output" == *'Unknown service "nginx".'* ]]
}

@test "install, status, and logs need root" {
  [ "$EUID" -ne 0 ] || skip "running as root"
  run "$INSTALLER" install --hostname rc.test --tls internal --yes
  [ "$status" -eq 1 ]
  [[ "$output" == *"The installer needs root"* ]]
  [[ "$output" == *"Next: rerun it with sudo"* ]]
}

# --- Output rules (brand guide section 13) ----------------------------------------------------

@test "marks are colored on a terminal" {
  command -v script >/dev/null || skip "script(1) is not installed"
  run script -qec "env -u NO_COLOR '$INSTALLER' install --bogus" /dev/null
  [[ "$output" == *$'\033[31m'* ]]
}

@test "with NO_COLOR set, output has no ANSI escape sequences even on a terminal" {
  command -v script >/dev/null || skip "script(1) is not installed"
  run script -qec "NO_COLOR=1 '$INSTALLER' install --bogus" /dev/null
  [[ "$output" == *"✕ Unknown option --bogus"* ]]
  [[ "$output" != *$'\033'* ]]
}

@test "output is plain when stdout is not a terminal" {
  load_installer
  run ok "Done."
  [ "$output" = "✓ Done." ]
}

@test "a port in use prints the failed step, the likely cause, and a next command" {
  load_installer
  port_in_use() { [[ "$1" == 443 ]]; }
  run check_ports
  [ "$status" -eq 1 ]
  [[ "$output" == *"✕ Port 443 is already in use."* ]]
  [[ "$output" == *"Likely cause: another web server"* ]]
  [[ "$output" == *"Next: find it with \"sudo ss -ltnp 'sport = :443'\""* ]]
}

# --- Operating system --------------------------------------------------------------------------

@test "supported distributions pass the OS check" {
  load_installer
  uname() { echo x86_64; }
  for release in "ubuntu 22.04" "ubuntu 24.04" "debian 12" "debian 13" "rhel 9.4" "rocky 9.5" "almalinux 9.6"; do
    os_release ${release% *} ${release#* } "Test ${release}"
    run check_os
    [ "$status" -eq 0 ]
    [[ "$output" == *"✓ Test ${release} on x86_64 is supported."* ]]
  done
}

@test "unsupported distributions fail the OS check with the supported list" {
  load_installer
  uname() { echo x86_64; }
  for release in "ubuntu 20.04" "debian 11" "centos 7" "rocky 8.9" "fedora 41"; do
    os_release ${release% *} ${release#* } "Test ${release}"
    run check_os
    [ "$status" -eq 1 ]
    [[ "$output" == *"✕ Test ${release} isn't supported."* ]]
    [[ "$output" == *"Next: install on Ubuntu 22.04 or 24.04, Debian 12 or 13"* ]]
  done
}

@test "unsupported architectures fail the OS check" {
  load_installer
  uname() { echo armv7l; }
  os_release ubuntu 24.04 "Ubuntu 24.04"
  run check_os
  [ "$status" -eq 1 ]
  [[ "$output" == *"The armv7l architecture isn't supported."* ]]
}

# --- Settings ----------------------------------------------------------------------------------

@test "hostnames are validated" {
  load_installer
  for name in rc.test rollcall.example.com rollcall.local a-b.c-d.example; do
    valid_hostname "$name"
  done
  for name in localhost "rc test.example" -rc.example.com rc.example.com- "https://rc.test" ""; do
    ! valid_hostname "$name"
  done
}

@test "with --yes, a missing hostname fails and names the option" {
  load_installer
  OPT_YES=1
  run choose_settings
  [ "$status" -eq 1 ]
  [[ "$output" == *"No hostname was given."* ]]
  [[ "$output" == *"--hostname <name>"* ]]
}

@test "with --yes, Let's Encrypt without an email fails" {
  load_installer
  OPT_YES=1 OPT_HOSTNAME=rc.test OPT_TLS=letsencrypt
  run choose_settings
  [ "$status" -eq 1 ]
  [[ "$output" == *"No email was given for Let's Encrypt."* ]]
}

@test "the hostname is lowercased" {
  load_installer
  OPT_YES=1 OPT_HOSTNAME=RC.Example.COM OPT_TLS=internal
  choose_settings
  [ "$OPT_HOSTNAME" = rc.example.com ]
}

# --- Certificates ------------------------------------------------------------------------------

make_cert() {
  openssl req -x509 -newkey rsa:2048 -nodes -days "${3:-30}" -subj "/CN=$1" -addext "subjectAltName=DNS:$1" \
    -keyout "$TEST_DIR/$2.key" -out "$TEST_DIR/$2.pem" 2>/dev/null
}

@test "a matching certificate and key for the hostname pass" {
  load_installer
  make_cert rc.test good
  OPT_TLS=custom OPT_HOSTNAME=rc.test OPT_CERT="$TEST_DIR/good.pem" OPT_KEY="$TEST_DIR/good.key"
  run check_certificate
  [ "$status" -eq 0 ]
  [[ "$output" == *"✓ The certificate matches the key and covers rc.test."* ]]
}

@test "a key from another certificate fails" {
  load_installer
  make_cert rc.test good
  make_cert rc.test other
  OPT_TLS=custom OPT_HOSTNAME=rc.test OPT_CERT="$TEST_DIR/good.pem" OPT_KEY="$TEST_DIR/other.key"
  run check_certificate
  [ "$status" -eq 1 ]
  [[ "$output" == *"The key doesn't match the certificate."* ]]
}

@test "a certificate for another hostname fails" {
  load_installer
  make_cert other.test good
  OPT_TLS=custom OPT_HOSTNAME=rc.test OPT_CERT="$TEST_DIR/good.pem" OPT_KEY="$TEST_DIR/good.key"
  run check_certificate
  [ "$status" -eq 1 ]
  [[ "$output" == *"The certificate isn't valid for rc.test."* ]]
}

@test "a missing certificate file fails" {
  load_installer
  OPT_TLS=custom OPT_HOSTNAME=rc.test OPT_CERT="$TEST_DIR/none.pem" OPT_KEY="$TEST_DIR/none.key"
  run check_certificate
  [ "$status" -eq 1 ]
  [[ "$output" == *"Couldn't read the certificate"* ]]
}

# --- Secrets and configuration -----------------------------------------------------------------

@test "secrets are non-empty, distinct, and private" {
  load_installer
  chown() { :; }
  OPT_HOSTNAME=rc.test OPT_TLS=internal
  run write_env
  [ "$status" -eq 0 ]
  [ "$(stat -c %a "$ROLLCALL_DIR/.env")" = 600 ]

  INSTALL_DIR="$ROLLCALL_DIR"
  local password key token
  password="$(env_value POSTGRES_PASSWORD)"
  key="$(env_value ROLLCALL_MASTER_KEY)"
  token="$(env_value ROLLCALL_SETUP_TOKEN)"
  [[ "$password" =~ ^[0-9a-f]{64}$ ]]
  [ "$(printf '%s' "$key" | base64 -d | wc -c)" -eq 32 ]
  [[ "$token" =~ ^[A-Z2-7]{39}$ ]]
  [ "$password" != "$key" ] && [ "$key" != "$token" ]
}

@test "a rerun after a failed attempt keeps the existing secrets" {
  load_installer
  chown() { :; }
  OPT_HOSTNAME=rc.test OPT_TLS=internal
  write_env >/dev/null
  local before
  before="$(grep -E '^(POSTGRES_PASSWORD|ROLLCALL_MASTER_KEY|ROLLCALL_SETUP_TOKEN)=' "$ROLLCALL_DIR/.env")"

  OPT_HOSTNAME=other.test
  run write_env
  [[ "$output" == *"Kept the secrets from the previous attempt"* ]]
  [ "$(grep -E '^(POSTGRES_PASSWORD|ROLLCALL_MASTER_KEY|ROLLCALL_SETUP_TOKEN)=' "$ROLLCALL_DIR/.env")" = "$before" ]
  grep -q '^ROLLCALL_HOSTNAME=other.test$' "$ROLLCALL_DIR/.env"
}

@test "an installed host is left alone and pointed to update" {
  load_installer
  echo "secret" >"$ROLLCALL_DIR/.env"
  touch "$ROLLCALL_DIR/.installed"
  run check_not_installed
  [ "$status" -eq 0 ]
  [[ "$output" == *"Rollcall is already installed in $ROLLCALL_DIR. Nothing was changed."* ]]
  [[ "$output" == *"install.sh update"* ]]
  [ "$(cat "$ROLLCALL_DIR/.env")" = secret ]
}

render_with() {
  printf 'ROLLCALL_VERSION=1.2.3\nROLLCALL_HOSTNAME=rc.test\nROLLCALL_TLS_MODE=%s\nROLLCALL_ACME_EMAIL=%s\nROLLCALL_IMAGE_PREFIX=ghcr.io/salmonllama\n' \
    "$1" "${2:-}" >"$ROLLCALL_DIR/.env"
  render_template "$BATS_TEST_DIRNAME/../Caddyfile.tmpl" "$TEST_DIR/Caddyfile"
  render_template "$BATS_TEST_DIRNAME/../docker-compose.yml.tmpl" "$TEST_DIR/docker-compose.yml"
}

@test "templates render the hostname, version, and each TLS mode" {
  load_installer
  INSTALL_DIR="$ROLLCALL_DIR"

  render_with internal
  grep -q '^rc.test {$' "$TEST_DIR/Caddyfile"
  grep -q '^	tls internal$' "$TEST_DIR/Caddyfile"
  grep -q 'image: ghcr.io/salmonllama/rollcall-app:1.2.3$' "$TEST_DIR/docker-compose.yml"
  grep -q 'image: ghcr.io/salmonllama/rollcall-worker:1.2.3$' "$TEST_DIR/docker-compose.yml"
  ! grep -q '{{' "$TEST_DIR/Caddyfile" "$TEST_DIR/docker-compose.yml"

  render_with letsencrypt 'ops+rollcall&it@example.com'
  grep -qF '	tls ops+rollcall&it@example.com' "$TEST_DIR/Caddyfile"

  render_with custom
  grep -q '^	tls /etc/rollcall/tls/cert.pem /etc/rollcall/tls/key.pem$' "$TEST_DIR/Caddyfile"
}

@test "the rendered Compose file publishes no database port" {
  load_installer
  INSTALL_DIR="$ROLLCALL_DIR"
  render_with internal
  # Only the proxy has a ports section.
  [ "$(grep -c '^    ports:' "$TEST_DIR/docker-compose.yml")" -eq 1 ]
  ! grep -q '5432:' "$TEST_DIR/docker-compose.yml"
}

# --- Update, backup, restore, uninstall --------------------------------------------------------

@test "destructive commands need --yes when there is no terminal" {
  load_installer
  COMMAND=uninstall
  run confirm "This deletes everything." </dev/null
  if { : </dev/tty; } 2>/dev/null; then skip "a terminal is attached"; fi
  [ "$status" -eq 1 ]
  [[ "$output" == *"uninstall needs confirmation, but there's no terminal to ask on."* ]]
  [[ "$output" == *"--yes"* ]]
}

@test "confirm passes with --yes" {
  load_installer
  OPT_YES=1
  run confirm "This deletes everything."
  [ "$status" -eq 0 ]
}

@test "restore rejects a file that isn't a Rollcall backup" {
  load_installer
  INSTALL_DIR="$ROLLCALL_DIR"
  require_root() { :; }
  touch "$ROLLCALL_DIR/.installed"
  echo "not a tarball" >"$TEST_DIR/backup.tar.gz"
  ARG="$TEST_DIR/backup.tar.gz"
  run cmd_restore
  [ "$status" -eq 1 ]
  [[ "$output" == *"isn't a Rollcall backup."* ]]
}

@test "restore reports a missing archive" {
  load_installer
  INSTALL_DIR="$ROLLCALL_DIR"
  require_root() { :; }
  touch "$ROLLCALL_DIR/.installed"
  ARG="$TEST_DIR/missing.tar.gz"
  run cmd_restore
  [ "$status" -eq 1 ]
  [[ "$output" == *"Couldn't read the backup archive"* ]]
}

@test "backups go outside the install directory so uninstall --purge keeps them" {
  load_installer
  unset ROLLCALL_BACKUP_DIR
  [ "$(backup_dir)" = /var/backups/rollcall ]
  OPT_OUTPUT=/srv/backups
  [ "$(backup_dir)" = /srv/backups ]
}

@test "commands other than install need a finished install" {
  load_installer
  INSTALL_DIR="$ROLLCALL_DIR"
  run require_installed
  [ "$status" -eq 1 ]
  [[ "$output" == *"Rollcall isn't installed in $ROLLCALL_DIR."* ]]
}

@test "the version of a downloaded installer is read from its source" {
  load_installer
  printf '#!/usr/bin/env bash\nROLLCALL_VERSION="1.4.0"\n' >"$TEST_DIR/latest.sh"
  [ "$(installer_version "$TEST_DIR/latest.sh")" = 1.4.0 ]
}
