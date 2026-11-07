#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Rollcall installer. Turns a clean Linux host into a running Rollcall stack served over HTTPS.
#
#   curl -fsSL https://github.com/Salmonllama/Rollcall/releases/latest/download/install.sh | sudo bash
#
# Run "install.sh help" for every subcommand. Everything below is wrapped in functions and
# main runs on the last line, so a partly downloaded script never runs half an install.

set -euo pipefail
# Bash 5.2 treats "&" in ${var//pattern/replacement} as the match; templates need it literal.
shopt -u patsub_replacement 2>/dev/null || true

# Set to the release version when a release is built; "edge" tracks main.
ROLLCALL_VERSION="edge"

REPO_SLUG="Salmonllama/Rollcall"
MIN_DOCKER_MAJOR=25
MIN_COMPOSE="2.20"

INSTALL_DIR="${ROLLCALL_DIR:-/opt/rollcall}"
OS_RELEASE_FILE="${ROLLCALL_OS_RELEASE:-/etc/os-release}"
LOG_FILE="${ROLLCALL_LOG:-/var/log/rollcall-install.log}"
IMAGE_PREFIX="${ROLLCALL_IMAGE_PREFIX:-ghcr.io/salmonllama}"

# Options, filled by parse_args.
COMMAND="install"
OPT_HOSTNAME=""
OPT_TLS=""
OPT_EMAIL=""
OPT_CERT=""
OPT_KEY=""
OPT_YES=0
OPT_PURGE=0
OPT_FOLLOW=0
OPT_OUTPUT=""
ARG=""

OS_ID=""
OS_VERSION=""
OS_NAME=""

# ---------------------------------------------------------------------------------------------
# Output (brand guide section 13): each step prints its name, then ✓ or ✕ with one line.
# Color only on the marks, and none when NO_COLOR is set or stdout is not a terminal.
# ---------------------------------------------------------------------------------------------

setup_output() {
  if [[ -t 1 && -z "${NO_COLOR:-}" ]]; then
    GREEN=$'\033[32m'
    RED=$'\033[31m'
    RESET=$'\033[0m'
  else
    GREEN=""
    RED=""
    RESET=""
  fi
}

step() { printf '%s\n' "$1"; }
ok() { printf '%s✓%s %s\n' "$GREEN" "$RESET" "$1"; }

# fail <what failed> <likely cause> <next command or action>
fail() {
  printf '%s✕%s %s\n' "$RED" "$RESET" "$1" >&2
  [[ -n "${2:-}" ]] && printf '  Likely cause: %s\n' "$2" >&2
  [[ -n "${3:-}" ]] && printf '  Next: %s\n' "$3" >&2
  exit 1
}

# usage_error <message> prints the problem and how to get help, then exits 2.
usage_error() {
  printf '%s✕%s %s\n' "$RED" "$RESET" "$1" >&2
  if [[ "$COMMAND" =~ ^(install|update|backup|restore|status|logs|admin-reset|uninstall)$ ]]; then
    printf '  Next: run "install.sh %s --help" to see the options.\n' "$COMMAND" >&2
  else
    printf '  Next: run "install.sh help" to see the commands.\n' >&2
  fi
  exit 2
}

# Runs a command with its output appended to the log, so the terminal shows only the steps.
logged() {
  {
    printf '\n$ %s\n' "$*"
    "$@"
  } >>"$LOG_FILE" 2>&1
}

log_hint() { printf 'see the last lines of %s for details: sudo tail -n 50 %s' "$LOG_FILE" "$LOG_FILE"; }

# ---------------------------------------------------------------------------------------------
# Help
# ---------------------------------------------------------------------------------------------

help_main() {
  cat <<EOF
Rollcall installer ${ROLLCALL_VERSION}

Usage: install.sh [command] [options]

Commands:
  install             Install Rollcall on this host (the default)
  update              Update to the latest release, with a backup first
  backup              Write a backup archive of the database and configuration
  restore <archive>   Restore a backup archive
  status              Show the state of each service
  logs [service]      Show recent logs from the stack, or from one service
  admin-reset <upn>   Give a user the Admin role again (available once sign-in exists)
  uninstall           Remove Rollcall, keeping data unless you add --purge
  help                Show this help

Run "install.sh <command> --help" for the options of one command.
Rollcall is installed in ${INSTALL_DIR}.
EOF
}

help_install() {
  cat <<'EOF'
Usage: install.sh install [options]

Installs Docker if it's missing, asks for the hostname and certificate type, generates
secrets, and starts Rollcall. Every question has an option, for unattended installs.

Options:
  --hostname <name>     Hostname users open in their browser, for example rollcall.example.com
  --tls <mode>          How Rollcall gets its HTTPS certificate:
                          letsencrypt  Let's Encrypt (needs public DNS and port 80 open)
                          custom       Your own certificate and key files
                          internal     A local certificate from Caddy, for test machines
  --email <address>     Contact email for Let's Encrypt (with --tls letsencrypt)
  --cert <path>         PEM certificate, full chain first (with --tls custom)
  --key <path>          PEM private key for that certificate (with --tls custom)
  -y, --yes             Don't ask questions; fail if a required option is missing
  -h, --help            Show this help
EOF
}

help_update() {
  cat <<'EOF'
Usage: install.sh update [options]

Downloads the latest installer, takes a backup, pulls the matching images, and restarts
Rollcall. If the new version fails to start, the previous version keeps running and the
backup path is printed.

Options:
  -y, --yes    Don't ask for confirmation
  -h, --help   Show this help
EOF
}

help_backup() {
  cat <<'EOF'
Usage: install.sh backup [options]

Writes rollcall-backup-<timestamp>.tar.gz with a database dump, .env, and the rendered
configuration. The archive contains the master key, so store it like a password.

Options:
  --output <dir>   Directory to write the archive to (default: /var/backups/rollcall)
  -h, --help       Show this help
EOF
}

help_restore() {
  cat <<'EOF'
Usage: install.sh restore <archive> [options]

Stops Rollcall, restores the database and configuration from a backup archive, and starts
it again. Everything changed since the backup is lost.

Options:
  -y, --yes    Don't ask for confirmation
  -h, --help   Show this help
EOF
}

help_status() {
  cat <<'EOF'
Usage: install.sh status

Shows each service's state and health, and whether Rollcall is ready.

Options:
  -h, --help   Show this help
EOF
}

help_logs() {
  cat <<'EOF'
Usage: install.sh logs [service] [options]

Shows the last 200 log lines from every service, or from one: proxy, app, worker, or db.

Options:
  -f, --follow   Keep printing new log lines until you press Ctrl+C
  -h, --help     Show this help
EOF
}

help_admin_reset() {
  cat <<'EOF'
Usage: install.sh admin-reset <upn>

Gives the user with this UPN the Admin role again, for when no one can manage roles.
Available once portal sign-in exists.

Options:
  -h, --help   Show this help
EOF
}

help_uninstall() {
  cat <<'EOF'
Usage: install.sh uninstall [options]

Stops and removes the Rollcall containers. The database, certificates, and configuration
are kept unless you add --purge.

Options:
  --purge      Also delete all data, including the database and /opt/rollcall
  -y, --yes    Don't ask for confirmation
  -h, --help   Show this help
EOF
}

show_help() {
  case "$1" in
    install) help_install ;;
    update) help_update ;;
    backup) help_backup ;;
    restore) help_restore ;;
    status) help_status ;;
    logs) help_logs ;;
    admin-reset) help_admin_reset ;;
    uninstall) help_uninstall ;;
    *) help_main ;;
  esac
}

# ---------------------------------------------------------------------------------------------
# Arguments
# ---------------------------------------------------------------------------------------------

# Options each command accepts, so a typo fails loudly instead of being ignored.
allowed_options() {
  case "$1" in
    install) echo "--hostname --tls --email --cert --key --yes" ;;
    update | restore) echo "--yes" ;;
    backup) echo "--output" ;;
    logs) echo "--follow" ;;
    uninstall) echo "--purge --yes" ;;
    *) echo "" ;;
  esac
}

parse_args() {
  local positional=()

  if [[ $# -gt 0 ]]; then
    case "$1" in
      install | update | backup | restore | status | logs | admin-reset | uninstall)
        COMMAND="$1"
        shift
        ;;
      help)
        COMMAND="help"
        shift
        ;;
      --version)
        printf 'Rollcall installer %s\n' "$ROLLCALL_VERSION"
        exit 0
        ;;
      -h | --help)
        help_main
        exit 0
        ;;
      -*) ;;
      *)
        COMMAND="$1"
        usage_error "Unknown command \"$1\"."
        ;;
    esac
  fi

  if [[ "$COMMAND" == help ]]; then
    show_help "${1:-}"
    exit 0
  fi

  while [[ $# -gt 0 ]]; do
    local option="$1"
    case "$option" in
      -h | --help)
        show_help "$COMMAND"
        exit 0
        ;;
      -y) option="--yes" ;;
      -f) option="--follow" ;;
      --*=*)
        set -- "${option%%=*}" "${option#*=}" "${@:2}"
        continue
        ;;
      -*) ;;
      *)
        positional+=("$1")
        shift
        continue
        ;;
    esac

    if [[ " $(allowed_options "$COMMAND") " != *" $option "* ]]; then
      usage_error "Unknown option $1 for $COMMAND."
    fi

    case "$option" in
      --yes) OPT_YES=1 ;;
      --purge) OPT_PURGE=1 ;;
      --follow) OPT_FOLLOW=1 ;;
      --hostname | --tls | --email | --cert | --key | --output)
        [[ $# -ge 2 && -n "$2" ]] || usage_error "Option $option needs a value."
        case "$option" in
          --hostname) OPT_HOSTNAME="$2" ;;
          --tls) OPT_TLS="$2" ;;
          --email) OPT_EMAIL="$2" ;;
          --cert) OPT_CERT="$2" ;;
          --key) OPT_KEY="$2" ;;
          --output) OPT_OUTPUT="$2" ;;
        esac
        shift
        ;;
    esac
    shift
  done

  case "$COMMAND" in
    restore | admin-reset)
      local what="an archive path"
      [[ "$COMMAND" == admin-reset ]] && what="a UPN"
      [[ ${#positional[@]} -eq 1 ]] || usage_error "$COMMAND needs exactly one argument: $what."
      ARG="${positional[0]}"
      ;;
    logs)
      [[ ${#positional[@]} -le 1 ]] || usage_error "logs takes at most one service name."
      ARG="${positional[0]:-}"
      if [[ -n "$ARG" && ! "$ARG" =~ ^(proxy|app|worker|db)$ ]]; then
        usage_error "Unknown service \"$ARG\". Use proxy, app, worker, or db."
      fi
      ;;
    *)
      [[ ${#positional[@]} -eq 0 ]] || usage_error "Unexpected argument \"${positional[0]}\"."
      ;;
  esac

  if [[ -n "$OPT_TLS" && ! "$OPT_TLS" =~ ^(letsencrypt|custom|internal)$ ]]; then
    usage_error "Unknown TLS mode \"$OPT_TLS\". Use letsencrypt, custom, or internal."
  fi
}

# ---------------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------------

require_root() {
  [[ $EUID -eq 0 ]] || fail "The installer needs root to install Docker and write to $INSTALL_DIR." \
    "It was run as a regular user." "rerun it with sudo, for example: curl -fsSL <installer-url> | sudo bash"
}

compose() {
  docker compose --project-directory "$INSTALL_DIR" -f "$INSTALL_DIR/docker-compose.yml" "$@"
}

# env_value <key> reads one value from the installed .env without sourcing it.
env_value() {
  local line
  line="$(grep -m1 "^$1=" "$INSTALL_DIR/.env" 2>/dev/null || true)"
  printf '%s' "${line#*=}"
}

random_hex() { od -An -tx1 -N"$1" /dev/urandom | tr -d ' \n'; }
random_base64() { head -c "$1" /dev/urandom | base64 -w0; }
random_base32() { head -c "$1" /dev/urandom | base32 -w0 | tr -d '='; }

# version_at_least <have> <want> compares dotted versions numerically.
version_at_least() {
  [[ "$(printf '%s\n%s\n' "$2" "$1" | sort -V | head -n1)" == "$2" ]]
}

# True when a terminal is available for questions, even when the script arrives on stdin.
can_prompt() {
  [[ $OPT_YES -eq 0 ]] && { : </dev/tty; } 2>/dev/null
}

# ask <prompt> <default> prints the answer.
ask() {
  local answer
  printf '%s' "$1" >/dev/tty
  [[ -n "$2" ]] && printf ' [%s]' "$2" >/dev/tty
  printf ': ' >/dev/tty
  IFS= read -r answer </dev/tty || true
  printf '%s' "${answer:-$2}"
}

valid_hostname() {
  [[ "$1" =~ ^([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z][A-Za-z0-9-]{0,62}$ ]] && [[ ${#1} -le 253 ]]
}

valid_email() { [[ "$1" =~ ^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$ ]]; }

port_in_use() {
  if command -v ss >/dev/null 2>&1; then
    [[ -n "$(ss -Hltn "sport = :$1" 2>/dev/null)" ]]
  else
    # Minimal images may lack iproute2; a successful local connection means something listens.
    (: </dev/tcp/127.0.0.1/"$1") 2>/dev/null
  fi
}

script_dir() {
  local source="${BASH_SOURCE[0]:-}"
  [[ -n "$source" && -f "$source" ]] && cd "$(dirname "$source")" && pwd
}

# Where the templates for this version live on GitHub when the installer was piped from curl.
source_ref() { [[ "$ROLLCALL_VERSION" == edge ]] && echo "main" || echo "v$ROLLCALL_VERSION"; }

# fetch_installer_file <name> <destination> copies a file that ships next to install.sh, or
# downloads it for this installer's version.
fetch_installer_file() {
  local dir
  dir="$(script_dir || true)"
  if [[ -n "$dir" && -f "$dir/$1" ]]; then
    cp "$dir/$1" "$2"
  else
    logged curl -fsSL --retry 3 -o "$2" "https://raw.githubusercontent.com/$REPO_SLUG/$(source_ref)/install/$1"
  fi
}

# ---------------------------------------------------------------------------------------------
# Install steps
# ---------------------------------------------------------------------------------------------

# os_field <name> reads one field from os-release without sourcing the file.
os_field() {
  sed -n "s/^$1=//p" "$OS_RELEASE_FILE" | head -n1 | sed -e 's/^"//' -e 's/"$//'
}

check_os() {
  step "Checking the operating system"
  [[ -r "$OS_RELEASE_FILE" ]] || fail "Couldn't identify the operating system." \
    "$OS_RELEASE_FILE is missing." "install on Ubuntu 22.04 or 24.04, Debian 12 or 13, or RHEL, Rocky, or AlmaLinux 9."

  OS_ID="$(os_field ID)"
  OS_VERSION="$(os_field VERSION_ID)"
  OS_NAME="$(os_field PRETTY_NAME)"
  [[ -n "$OS_NAME" ]] || OS_NAME="$OS_ID $OS_VERSION"

  local supported=0
  case "$OS_ID" in
    ubuntu) [[ "$OS_VERSION" == 22.04 || "$OS_VERSION" == 24.04 ]] && supported=1 ;;
    debian) [[ "$OS_VERSION" == 12 || "$OS_VERSION" == 13 ]] && supported=1 ;;
    rhel | rocky | almalinux) [[ "${OS_VERSION%%.*}" == 9 ]] && supported=1 ;;
  esac
  [[ $supported -eq 1 ]] || fail "$OS_NAME isn't supported." \
    "Rollcall is tested on specific distributions only." \
    "install on Ubuntu 22.04 or 24.04, Debian 12 or 13, or RHEL, Rocky, or AlmaLinux 9."

  local arch
  arch="$(uname -m)"
  [[ "$arch" == x86_64 || "$arch" == aarch64 ]] || fail "The $arch architecture isn't supported." \
    "Rollcall images are built for x86_64 and aarch64 only." "install on an x86_64 or aarch64 host."

  ok "$OS_NAME on $arch is supported."
}

# The marker is written only after a successful install, so a failed attempt can be rerun.
check_not_installed() {
  if [[ -f "$INSTALL_DIR/.installed" ]]; then
    ok "Rollcall is already installed in $INSTALL_DIR. Nothing was changed."
    printf '  To upgrade, run: sudo bash %s/install.sh update\n' "$INSTALL_DIR"
    exit 0
  fi
}

check_ports() {
  step "Checking that ports 80 and 443 are free"
  local port
  for port in 80 443; do
    if port_in_use "$port"; then
      fail "Port $port is already in use." \
        "another web server, such as nginx or Apache, is running on this host." \
        "find it with \"sudo ss -ltnp 'sport = :$port'\", stop it, then run the installer again."
    fi
  done
  ok "Ports 80 and 443 are free."
}

install_docker_packages() {
  case "$OS_ID" in
    ubuntu | debian)
      local codename
      codename="$(os_field VERSION_CODENAME)"
      logged apt-get update
      logged env DEBIAN_FRONTEND=noninteractive apt-get install -y ca-certificates curl
      logged install -m 0755 -d /etc/apt/keyrings
      logged curl -fsSL "https://download.docker.com/linux/$OS_ID/gpg" -o /etc/apt/keyrings/docker.asc
      chmod a+r /etc/apt/keyrings/docker.asc
      printf 'deb [arch=%s signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/%s %s stable\n' \
        "$(dpkg --print-architecture)" "$OS_ID" "$codename" >/etc/apt/sources.list.d/docker.list
      logged apt-get update
      logged env DEBIAN_FRONTEND=noninteractive apt-get install -y \
        docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
      ;;
    rhel | rocky | almalinux)
      logged dnf -y install dnf-plugins-core
      logged dnf config-manager --add-repo https://download.docker.com/linux/rhel/docker-ce.repo
      # --allowerasing replaces podman's runc and buildah packages if they are installed.
      logged dnf -y install --allowerasing docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
      ;;
  esac
  logged systemctl enable --now docker
}

ensure_docker() {
  step "Checking Docker Engine and the Compose plugin"
  if ! command -v docker >/dev/null 2>&1; then
    step "Installing Docker Engine from Docker's official repository"
    install_docker_packages || fail "Couldn't install Docker Engine." \
      "the host can't reach download.docker.com, or the package manager is locked." "$(log_hint)"
  fi

  local engine compose_version
  engine="$(docker version --format '{{.Server.Version}}' 2>/dev/null || true)"
  [[ -n "$engine" ]] || fail "Docker is installed but the Docker daemon isn't running." \
    "the docker service is stopped." "run \"sudo systemctl enable --now docker\", then run the installer again."
  [[ "${engine%%.*}" -ge $MIN_DOCKER_MAJOR ]] || fail "Docker Engine $engine is too old." \
    "Rollcall needs Docker Engine $MIN_DOCKER_MAJOR or later." \
    "upgrade Docker from https://docs.docker.com/engine/install/, then run the installer again."

  compose_version="$(docker compose version --short 2>/dev/null || true)"
  [[ -n "$compose_version" ]] || fail "The Docker Compose plugin is missing." \
    "Docker was installed without the docker-compose-plugin package." \
    "install docker-compose-plugin from Docker's repository, then run the installer again."
  version_at_least "${compose_version#v}" "$MIN_COMPOSE" || fail "Docker Compose $compose_version is too old." \
    "Rollcall needs Docker Compose $MIN_COMPOSE or later." \
    "upgrade docker-compose-plugin, then run the installer again."

  ok "Docker Engine $engine and Docker Compose ${compose_version#v} are ready."
}

choose_settings() {
  if [[ -z "$OPT_HOSTNAME" ]] && can_prompt; then
    OPT_HOSTNAME="$(ask "Hostname users will open, for example rollcall.example.com" "")"
  fi
  [[ -n "$OPT_HOSTNAME" ]] || fail "No hostname was given." "the installer ran without a terminal or with --yes." \
    "rerun with --hostname <name>, for example --hostname rollcall.example.com."
  valid_hostname "$OPT_HOSTNAME" || fail "\"$OPT_HOSTNAME\" isn't a valid hostname." \
    "a hostname needs at least one dot and only letters, digits, and hyphens." \
    "rerun with a full hostname, for example --hostname rollcall.example.com."
  OPT_HOSTNAME="${OPT_HOSTNAME,,}"

  if [[ -z "$OPT_TLS" ]] && can_prompt; then
    {
      echo "How should Rollcall get its HTTPS certificate?"
      echo "  1) Let's Encrypt (needs public DNS for $OPT_HOSTNAME and port 80 open)"
      echo "  2) Your own certificate and key files"
      echo "  3) A local certificate from Caddy, for test machines (browsers warn until you trust it)"
    } >/dev/tty
    case "$(ask "Choose 1, 2, or 3" "1")" in
      1) OPT_TLS=letsencrypt ;;
      2) OPT_TLS=custom ;;
      3) OPT_TLS=internal ;;
      *) fail "That isn't one of the choices." "" "run the installer again and choose 1, 2, or 3." ;;
    esac
  fi
  [[ -n "$OPT_TLS" ]] || fail "No certificate type was given." "the installer ran without a terminal or with --yes." \
    "rerun with --tls letsencrypt, --tls custom, or --tls internal."

  case "$OPT_TLS" in
    letsencrypt)
      if [[ -z "$OPT_EMAIL" ]] && can_prompt; then
        OPT_EMAIL="$(ask "Email for Let's Encrypt expiry notices" "")"
      fi
      [[ -n "$OPT_EMAIL" ]] || fail "No email was given for Let's Encrypt." \
        "--tls letsencrypt needs a contact email." "rerun with --email <address>."
      valid_email "$OPT_EMAIL" || fail "\"$OPT_EMAIL\" isn't a valid email address." "" "rerun with --email <address>."
      ;;
    custom)
      if [[ -z "$OPT_CERT" ]] && can_prompt; then OPT_CERT="$(ask "Path to the PEM certificate (full chain)" "")"; fi
      if [[ -z "$OPT_KEY" ]] && can_prompt; then OPT_KEY="$(ask "Path to the PEM private key" "")"; fi
      [[ -n "$OPT_CERT" && -n "$OPT_KEY" ]] || fail "The certificate or key path is missing." \
        "--tls custom needs both files." "rerun with --cert <path> --key <path>."
      ;;
  esac
}

check_certificate() {
  [[ "$OPT_TLS" == custom ]] || return 0
  step "Checking the certificate and key"
  [[ -r "$OPT_CERT" ]] || fail "Couldn't read the certificate at $OPT_CERT." "the path is wrong." "rerun with the correct --cert path."
  [[ -r "$OPT_KEY" ]] || fail "Couldn't read the key at $OPT_KEY." "the path is wrong." "rerun with the correct --key path."
  command -v openssl >/dev/null 2>&1 || fail "openssl is missing, so the certificate can't be checked." \
    "the host has a minimal package set." "install the openssl package, then run the installer again."

  openssl x509 -in "$OPT_CERT" -noout 2>/dev/null || fail "$OPT_CERT isn't a PEM certificate." \
    "the file is in DER or PFX format, or is the key." "convert it to PEM, for example: openssl x509 -inform der -in cert.cer -out cert.pem"
  local cert_key key_key
  cert_key="$(openssl x509 -in "$OPT_CERT" -noout -pubkey 2>/dev/null | openssl sha256)"
  key_key="$(openssl pkey -in "$OPT_KEY" -pubout 2>/dev/null | openssl sha256)" || true
  [[ -n "$key_key" && "$cert_key" == "$key_key" ]] || fail "The key doesn't match the certificate." \
    "the key belongs to a different certificate, or is encrypted with a passphrase." \
    "rerun with the matching, unencrypted key file."
  if ! openssl x509 -in "$OPT_CERT" -noout -checkend 0 >/dev/null 2>&1; then
    fail "The certificate has expired." "" "renew it, then run the installer again."
  fi
  if ! openssl x509 -in "$OPT_CERT" -noout -checkhost "$OPT_HOSTNAME" 2>/dev/null | grep -q 'does match'; then
    fail "The certificate isn't valid for $OPT_HOSTNAME." "it was issued for a different name." \
      "use a certificate that covers $OPT_HOSTNAME, or rerun with the hostname it was issued for."
  fi
  ok "The certificate matches the key and covers $OPT_HOSTNAME."
}

write_env() {
  step "Generating secrets"
  # A previous attempt that failed part way may have written secrets already. Keep them, so
  # a database it initialized still accepts the password.
  local postgres_password master_key setup_token reused=0
  postgres_password="$(env_value POSTGRES_PASSWORD)"
  master_key="$(env_value ROLLCALL_MASTER_KEY)"
  setup_token="$(env_value ROLLCALL_SETUP_TOKEN)"
  if [[ -n "$postgres_password" && -n "$master_key" && -n "$setup_token" ]]; then
    reused=1
  else
    postgres_password="$(random_hex 32)"
    master_key="$(random_base64 32)"
    setup_token="$(random_base32 24)"
  fi

  (
    umask 077
    cat >"$INSTALL_DIR/.env" <<EOF
# Rollcall configuration and secrets. Generated by install.sh; keep this file private.
# Losing ROLLCALL_MASTER_KEY makes encrypted data in the database unreadable.
ROLLCALL_VERSION=$ROLLCALL_VERSION
ROLLCALL_HOSTNAME=$OPT_HOSTNAME
ROLLCALL_TLS_MODE=$OPT_TLS
ROLLCALL_ACME_EMAIL=$OPT_EMAIL
ROLLCALL_IMAGE_PREFIX=$IMAGE_PREFIX
POSTGRES_PASSWORD=$postgres_password
ROLLCALL_MASTER_KEY=$master_key
ROLLCALL_SETUP_TOKEN=$setup_token
EOF
  )
  chown root:root "$INSTALL_DIR/.env"
  chmod 0600 "$INSTALL_DIR/.env"
  if [[ $reused -eq 1 ]]; then
    ok "Kept the secrets from the previous attempt in $INSTALL_DIR/.env."
  else
    ok "Secrets saved to $INSTALL_DIR/.env, readable by root only."
  fi
}

copy_certificate() {
  mkdir -p "$INSTALL_DIR/tls"
  chmod 0700 "$INSTALL_DIR/tls"
  [[ "$OPT_TLS" == custom ]] || return 0
  install -m 0644 "$OPT_CERT" "$INSTALL_DIR/tls/cert.pem"
  install -m 0600 "$OPT_KEY" "$INSTALL_DIR/tls/key.pem"
}

tls_directive() {
  case "$(env_value ROLLCALL_TLS_MODE)" in
    letsencrypt) printf 'tls %s' "$(env_value ROLLCALL_ACME_EMAIL)" ;;
    custom) printf 'tls /etc/rollcall/tls/cert.pem /etc/rollcall/tls/key.pem' ;;
    internal) printf 'tls internal' ;;
  esac
}

# render_template <template> <output> replaces {{NAME}} placeholders.
render_template() {
  local content
  content="$(<"$1")"
  content="${content//\{\{IMAGE_PREFIX\}\}/$(env_value ROLLCALL_IMAGE_PREFIX)}"
  content="${content//\{\{VERSION\}\}/$(env_value ROLLCALL_VERSION)}"
  content="${content//\{\{HOSTNAME\}\}/$(env_value ROLLCALL_HOSTNAME)}"
  content="${content//\{\{TLS_DIRECTIVE\}\}/$(tls_directive)}"
  printf '%s\n' "$content" >"$2"
}

render_config() {
  step "Writing the Compose and Caddy configuration"
  local tmp
  tmp="$(mktemp -d)"
  local file
  for file in docker-compose.yml.tmpl Caddyfile.tmpl; do
    fetch_installer_file "$file" "$tmp/$file" || fail "Couldn't download the configuration templates for $ROLLCALL_VERSION." \
      "the host can't reach raw.githubusercontent.com." "$(log_hint)"
  done
  render_template "$tmp/docker-compose.yml.tmpl" "$INSTALL_DIR/docker-compose.yml"
  render_template "$tmp/Caddyfile.tmpl" "$INSTALL_DIR/Caddyfile"
  rm -rf "$tmp"
  copy_certificate

  # Keep a copy of the installer next to the stack for later commands such as backup.
  local dir
  dir="$(script_dir || true)"
  if [[ -n "$dir" && -f "$dir/install.sh" ]]; then
    [[ "$dir/install.sh" -ef "$INSTALL_DIR/install.sh" ]] || install -m 0755 "$dir/install.sh" "$INSTALL_DIR/install.sh"
  else
    fetch_installer_file install.sh "$INSTALL_DIR/install.sh" && chmod 0755 "$INSTALL_DIR/install.sh"
  fi
  logged compose config --quiet || fail "The rendered Compose file isn't valid." \
    "a template or .env value is malformed." "$(log_hint)"
  ok "Configuration written to $INSTALL_DIR."
}

pull_images() {
  step "Downloading the Rollcall $ROLLCALL_VERSION images"
  logged compose pull --quiet || fail "Couldn't download the container images." \
    "the host can't reach ghcr.io or Docker Hub, or the version doesn't exist." "$(log_hint)"
  ok "Images downloaded."
}

start_stack() {
  step "Starting Rollcall"
  logged compose up --detach --wait --wait-timeout 300 || fail "Rollcall didn't become healthy within 5 minutes." \
    "a container failed to start; its logs say why." \
    "run \"sudo bash $INSTALL_DIR/install.sh status\", then \"sudo bash $INSTALL_DIR/install.sh logs app\"."
  ok "All four services are running and healthy."
}

# readyz_json prints the app's /readyz body, fetched from inside the proxy container so it
# works before DNS or the certificate is in place.
readyz_json() {
  compose exec -T proxy wget -q -T 5 -O - http://app:8080/readyz 2>/dev/null
}

wait_for_ready() {
  step "Waiting for Rollcall to report ready"
  local ready="" _
  for _ in $(seq 1 30); do
    ready="$(readyz_json || true)"
    [[ "$ready" == *'"worker":"ok"'* ]] && break
    sleep 2
  done
  [[ "$ready" == *'"db":"ok"'*'"migrations":"current"'*'"worker":"ok"'* ]] || fail "Rollcall didn't report ready." \
    "the database or worker isn't working: ${ready:-no response}." \
    "run \"sudo bash $INSTALL_DIR/install.sh logs worker\" and \"... logs app\" to see why."
  ok "Database, migrations, and worker report ready."
}

check_https() {
  step "Checking HTTPS on $OPT_HOSTNAME"
  # A loopback probe of this host's own proxy: it confirms Caddy serves the site, so the
  # certificate is not verified here. Browsers verify it as usual.
  local _
  for _ in $(seq 1 15); do
    if curl -fsS --noproxy '*' --insecure --max-time 5 --resolve "$OPT_HOSTNAME:443:127.0.0.1" \
      -o /dev/null "https://$OPT_HOSTNAME/healthz" >>"$LOG_FILE" 2>&1; then
      ok "Caddy serves https://$OPT_HOSTNAME."
      return 0
    fi
    sleep 2
  done
  if [[ "$OPT_TLS" == letsencrypt ]]; then
    printf '%s✕%s Let'\''s Encrypt hasn'\''t issued a certificate yet.\n' "$RED" "$RESET"
    printf '  Likely cause: DNS for %s doesn'\''t point to this host yet, or port 80 is blocked.\n' "$OPT_HOSTNAME"
    printf '  Next: fix DNS or the firewall; Caddy keeps retrying. Watch with "sudo bash %s/install.sh logs proxy".\n' "$INSTALL_DIR"
    return 0
  fi
  fail "Caddy isn't serving https://$OPT_HOSTNAME." "the certificate or Caddyfile was rejected." \
    "run \"sudo bash $INSTALL_DIR/install.sh logs proxy\" to see why."
}

export_local_ca() {
  [[ "$OPT_TLS" == internal ]] || return 0
  compose cp proxy:/data/caddy/pki/authorities/local/root.crt "$INSTALL_DIR/local-ca.crt" >>"$LOG_FILE" 2>&1 || return 0
  chmod 0644 "$INSTALL_DIR/local-ca.crt"
}

print_summary() {
  local url="https://$OPT_HOSTNAME"
  echo
  ok "Rollcall $ROLLCALL_VERSION is installed."
  printf '  Setup token: %s\n' "$(env_value ROLLCALL_SETUP_TOKEN)"
  printf '  The setup wizard asks for this token. It is also in %s/.env.\n' "$INSTALL_DIR"
  if [[ "$OPT_TLS" == internal && -f "$INSTALL_DIR/local-ca.crt" ]]; then
    printf '  This host uses a local certificate. To stop browser warnings, trust %s/local-ca.crt\n' "$INSTALL_DIR"
    printf '  on each computer that opens Rollcall, and point %s at this host in DNS or /etc/hosts.\n' "$OPT_HOSTNAME"
  fi
  echo
  echo "$url"
  echo
  echo "Open $url to finish setup."
}

cmd_install() {
  require_root
  check_not_installed
  check_os
  choose_settings
  check_certificate
  check_ports
  ensure_docker
  mkdir -p "$INSTALL_DIR"
  chmod 0755 "$INSTALL_DIR"
  write_env
  render_config
  pull_images
  start_stack
  wait_for_ready
  check_https
  export_local_ca
  date -u +%Y-%m-%dT%H:%M:%SZ >"$INSTALL_DIR/.installed"
  print_summary
}

# ---------------------------------------------------------------------------------------------
# Other commands
# ---------------------------------------------------------------------------------------------

require_installed() {
  [[ -f "$INSTALL_DIR/.installed" ]] || fail "Rollcall isn't installed in $INSTALL_DIR." \
    "the install didn't finish, or Rollcall was uninstalled." "install it with: sudo bash install.sh install"
}

cmd_status() {
  require_root
  require_installed
  compose ps --all --format 'table {{.Service}}\t{{.Status}}\t{{.Image}}'
  echo
  local ready
  ready="$(readyz_json || true)"
  [[ -n "$ready" ]] || ready="the app did not answer"
  if [[ "$ready" == '{"db":"ok","migrations":"current","worker":"ok"}' ]]; then
    ok "Rollcall $(env_value ROLLCALL_VERSION) is ready at https://$(env_value ROLLCALL_HOSTNAME)."
  else
    fail "Rollcall isn't ready: $ready." "" \
      "run \"sudo bash $INSTALL_DIR/install.sh logs\" to see what failed."
  fi
}

cmd_logs() {
  require_root
  require_installed
  local args=(logs --tail 200)
  [[ $OPT_FOLLOW -eq 1 ]] && args+=(--follow)
  [[ -n "$ARG" ]] && args+=("$ARG")
  compose "${args[@]}"
}

cmd_admin_reset() {
  fail "admin-reset isn't available in this version." \
    "portal sign-in and roles arrive in the next release." "nothing to do yet; no one can sign in to reset."
}

# confirm <question> asks for "yes" unless --yes was given. Without a terminal, --yes is required.
confirm() {
  [[ $OPT_YES -eq 1 ]] && return 0
  { : </dev/tty; } 2>/dev/null || fail "$COMMAND needs confirmation, but there's no terminal to ask on." "" \
    "rerun with --yes to confirm: sudo bash $INSTALL_DIR/install.sh $COMMAND --yes"
  printf '%s\n' "$1" >/dev/tty
  [[ "$(ask "Type yes to continue" "")" == yes ]] || fail "Nothing was changed." "" ""
}

# --- Backup and restore ----------------------------------------------------------------------

# create_backup <dir> writes an archive and sets BACKUP_PATH.
create_backup() {
  local dir="$1" tmp stamp
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  BACKUP_PATH="$dir/rollcall-backup-$stamp.tar.gz"
  mkdir -p "$dir"
  chmod 0700 "$dir"
  tmp="$(mktemp -d)"
  mkdir "$tmp/config"

  logged compose up --detach --wait db || { rm -rf "$tmp"; return 1; }
  if ! compose exec -T db pg_dump -U rollcall -d rollcall --format=custom >"$tmp/db.dump" 2>>"$LOG_FILE"; then
    rm -rf "$tmp"
    return 1
  fi
  cp -p "$INSTALL_DIR/.env" "$INSTALL_DIR/docker-compose.yml" "$INSTALL_DIR/Caddyfile" "$tmp/config/"
  cp -rp "$INSTALL_DIR/tls" "$tmp/config/tls"
  printf 'format=1\nversion=%s\ncreated=%s\n' "$(env_value ROLLCALL_VERSION)" "$stamp" >"$tmp/manifest"

  (umask 077 && tar -czf "$BACKUP_PATH" -C "$tmp" manifest db.dump config) || { rm -rf "$tmp"; return 1; }
  chmod 0600 "$BACKUP_PATH"
  rm -rf "$tmp"
}

backup_dir() { printf '%s' "${OPT_OUTPUT:-${ROLLCALL_BACKUP_DIR:-/var/backups/rollcall}}"; }

cmd_backup() {
  require_root
  require_installed
  step "Backing up the database and configuration"
  create_backup "$(backup_dir)" || fail "Couldn't create the backup." \
    "the database container isn't running, or the disk is full." "$(log_hint)"
  ok "Backup written to $BACKUP_PATH."
  printf '  The archive contains the master key. Store it somewhere only admins can read.\n'
}

# restore_database <dump> replaces the database contents with a pg_dump archive.
restore_database() {
  logged compose stop proxy worker app
  logged compose up --detach --wait db
  compose exec -T db pg_restore -U rollcall -d rollcall --clean --if-exists --no-owner --single-transaction \
    <"$1" >>"$LOG_FILE" 2>&1
}

cmd_restore() {
  require_root
  require_installed
  local archive="$ARG" tmp
  [[ -r "$archive" ]] || fail "Couldn't read the backup archive $archive." "the path is wrong." \
    "check the path; backups are in $(backup_dir) by default."

  step "Checking the backup archive"
  tmp="$(mktemp -d)"
  if ! tar -xzf "$archive" -C "$tmp" 2>>"$LOG_FILE" || [[ ! -f "$tmp/manifest" || ! -f "$tmp/db.dump" || ! -f "$tmp/config/.env" ]]; then
    rm -rf "$tmp"
    fail "$archive isn't a Rollcall backup." "the file is damaged or was made by another tool." \
      "pick an archive named rollcall-backup-<timestamp>.tar.gz."
  fi
  local version created
  version="$(sed -n 's/^version=//p' "$tmp/manifest")"
  created="$(sed -n 's/^created=//p' "$tmp/manifest")"
  # 20261026T215308Z -> 2026-10-26 21:53 UTC
  created="$(sed -E 's/^([0-9]{4})([0-9]{2})([0-9]{2})T([0-9]{2})([0-9]{2}).*/\1-\2-\3 \4:\5 UTC/' <<<"$created")"
  ok "Backup of Rollcall $version from $created."

  confirm "This replaces all Rollcall data on this host with the backup from $created. Changes made since then are lost."

  step "Backing up the current state first"
  create_backup "$(backup_dir)" || fail "Couldn't back up the current state, so nothing was restored." \
    "the database container isn't running, or the disk is full." "$(log_hint)"
  local safety="$BACKUP_PATH"
  ok "Current state saved to $safety."

  step "Restoring the configuration"
  logged compose down
  # The database password comes from the backup, so the database volume starts fresh.
  logged docker volume rm rollcall_db-data || true
  install -m 0600 -o root -g root "$tmp/config/.env" "$INSTALL_DIR/.env"
  install -m 0644 "$tmp/config/docker-compose.yml" "$INSTALL_DIR/docker-compose.yml"
  install -m 0644 "$tmp/config/Caddyfile" "$INSTALL_DIR/Caddyfile"
  rm -rf "$INSTALL_DIR/tls"
  cp -rp "$tmp/config/tls" "$INSTALL_DIR/tls"
  ok "Configuration restored."

  step "Restoring the database"
  logged compose pull --quiet db app worker || true
  restore_database "$tmp/db.dump" || { rm -rf "$tmp"; fail "Couldn't restore the database." \
    "the archive's database dump is damaged." "restore the previous state with: sudo bash $INSTALL_DIR/install.sh restore $safety"; }
  rm -rf "$tmp"
  ok "Database restored."

  step "Starting Rollcall"
  logged compose up --detach --wait --wait-timeout 300 || fail "Rollcall didn't start after the restore." \
    "a container failed to start; its logs say why." "run \"sudo bash $INSTALL_DIR/install.sh logs app\"."
  ok "All four services are running and healthy."
  OPT_HOSTNAME="$(env_value ROLLCALL_HOSTNAME)"
  wait_for_ready
  echo
  ok "Restored Rollcall $version from $created."
  printf '  The state before the restore is saved in %s.\n' "$safety"
}

# --- Update ----------------------------------------------------------------------------------

latest_installer_url() {
  if [[ "$ROLLCALL_VERSION" == edge ]]; then
    printf 'https://raw.githubusercontent.com/%s/main/install/install.sh' "$REPO_SLUG"
  else
    printf 'https://github.com/%s/releases/latest/download/install.sh' "$REPO_SLUG"
  fi
}

installer_version() { sed -n 's/^ROLLCALL_VERSION="\(.*\)"$/\1/p' "$1" | head -n1; }

set_env_value() {
  sed -i "s|^$1=.*|$1=$2|" "$INSTALL_DIR/.env"
}

# rollback_update <dir> <backup> puts the previous version back after a failed update.
rollback_update() {
  step "Putting the previous version back"
  install -m 0600 -o root -g root "$1/.env" "$INSTALL_DIR/.env"
  install -m 0644 "$1/docker-compose.yml" "$INSTALL_DIR/docker-compose.yml"
  install -m 0644 "$1/Caddyfile" "$INSTALL_DIR/Caddyfile"
  install -m 0755 "$1/install.sh" "$INSTALL_DIR/install.sh" 2>/dev/null || true
  # A failed migration may have changed part of the schema, so the database goes back too.
  local tmp status=1
  tmp="$(mktemp -d)"
  if tar -xzf "$2" -C "$tmp" db.dump && restore_database "$tmp/db.dump" &&
    logged compose up --detach --wait --wait-timeout 300; then
    status=0
  fi
  rm -rf "$tmp"
  if [[ $status -eq 0 ]]; then
    ok "Rollcall $(env_value ROLLCALL_VERSION) is running again."
  else
    printf '%s✕%s Couldn'\''t start the previous version either.\n' "$RED" "$RESET" >&2
    printf '  Next: restore the backup with: sudo bash %s/install.sh restore %s\n' "$INSTALL_DIR" "$2" >&2
  fi
}

cmd_update() {
  require_root
  require_installed

  # Run the newest installer, so the update uses that version's templates and steps.
  if [[ "${ROLLCALL_UPDATE_SOURCE:-}" != local ]]; then
    step "Downloading the latest installer"
    local latest
    latest="$(mktemp)"
    logged curl -fsSL --retry 3 -o "$latest" "$(latest_installer_url)" || fail "Couldn't download the latest installer." \
      "the host can't reach github.com." "$(log_hint)"
    ok "Latest installer is version $(installer_version "$latest")."
    local args=(update)
    [[ $OPT_YES -eq 1 ]] && args+=(--yes)
    ROLLCALL_UPDATE_SOURCE=local exec bash "$latest" "${args[@]}"
  fi

  local current
  current="$(env_value ROLLCALL_VERSION)"
  if [[ "$current" == "$ROLLCALL_VERSION" && "$ROLLCALL_VERSION" != edge ]]; then
    ok "Rollcall $current is already the latest version."
    exit 0
  fi
  confirm "This updates Rollcall from $current to $ROLLCALL_VERSION. Rollcall is unavailable for a minute or two."

  step "Backing up before the update"
  create_backup "$(backup_dir)" || fail "Couldn't create the backup, so nothing was updated." \
    "the database container isn't running, or the disk is full." "$(log_hint)"
  local backup="$BACKUP_PATH"
  ok "Backup written to $backup."

  local previous
  previous="$(mktemp -d)"
  cp -p "$INSTALL_DIR/.env" "$INSTALL_DIR/docker-compose.yml" "$INSTALL_DIR/Caddyfile" "$previous/"
  cp -p "$INSTALL_DIR/install.sh" "$previous/" 2>/dev/null || true

  set_env_value ROLLCALL_VERSION "$ROLLCALL_VERSION"
  OPT_HOSTNAME="$(env_value ROLLCALL_HOSTNAME)"
  OPT_TLS="$(env_value ROLLCALL_TLS_MODE)"
  render_config
  step "Downloading the Rollcall $ROLLCALL_VERSION images"
  if ! logged compose pull --quiet; then
    install -m 0600 -o root -g root "$previous/.env" "$INSTALL_DIR/.env"
    install -m 0644 "$previous/docker-compose.yml" "$previous/Caddyfile" "$INSTALL_DIR/"
    rm -rf "$previous"
    fail "Couldn't download the Rollcall $ROLLCALL_VERSION images. Rollcall $current is still running." \
      "the host can't reach ghcr.io or Docker Hub." "$(log_hint)"
  fi
  ok "Images downloaded."

  step "Starting Rollcall $ROLLCALL_VERSION and running database migrations"
  if ! logged compose up --detach --wait --wait-timeout 300 || ! readyz_ok_with_retry; then
    printf '%s✕%s Rollcall %s didn'\''t start.\n' "$RED" "$RESET" "$ROLLCALL_VERSION" >&2
    printf '  Likely cause: a database migration or the new app failed; "install.sh logs app" shows why.\n' >&2
    rollback_update "$previous" "$backup"
    rm -rf "$previous"
    printf '  The backup taken before the update is %s.\n' "$backup" >&2
    exit 1
  fi
  rm -rf "$previous"
  ok "Rollcall $ROLLCALL_VERSION is running and ready."
}

readyz_ok_with_retry() {
  local _ ready
  for _ in $(seq 1 30); do
    ready="$(readyz_json || true)"
    [[ "$ready" == '{"db":"ok","migrations":"current","worker":"ok"}' ]] && return 0
    sleep 2
  done
  return 1
}

# --- Uninstall -------------------------------------------------------------------------------

cmd_uninstall() {
  require_root
  [[ -f "$INSTALL_DIR/docker-compose.yml" ]] || fail "Rollcall isn't installed in $INSTALL_DIR." "" ""
  if [[ $OPT_PURGE -eq 1 ]]; then
    confirm "This deletes Rollcall and all its data: the database, certificates, secrets, and $INSTALL_DIR. Backups in $(backup_dir) are kept."
  else
    confirm "This removes the Rollcall containers. The database and configuration are kept."
  fi

  step "Stopping and removing the containers"
  if [[ $OPT_PURGE -eq 1 ]]; then
    logged compose down --volumes --remove-orphans || fail "Couldn't remove the containers." "" "$(log_hint)"
    rm -rf "$INSTALL_DIR"
    ok "Rollcall and its data are removed. Backups in $(backup_dir) were kept."
  else
    logged compose down --remove-orphans || fail "Couldn't remove the containers." "" "$(log_hint)"
    rm -f "$INSTALL_DIR/.installed"
    ok "Containers removed. Data and configuration are kept in $INSTALL_DIR and the Docker volumes."
    printf '  To start Rollcall again with the same data, run: sudo bash %s/install.sh install\n' "$INSTALL_DIR"
  fi
}

main() {
  setup_output
  parse_args "$@"
  if [[ $EUID -eq 0 ]]; then
    mkdir -p "$(dirname "$LOG_FILE")"
    : >>"$LOG_FILE"
  fi
  case "$COMMAND" in
    install) cmd_install ;;
    status) cmd_status ;;
    logs) cmd_logs ;;
    admin-reset) cmd_admin_reset ;;
    update) cmd_update ;;
    backup) cmd_backup ;;
    restore) cmd_restore ;;
    uninstall) cmd_uninstall ;;
  esac
}

# bats sources this file with ROLLCALL_SOURCED=1 to test functions without running main.
if [[ "${ROLLCALL_SOURCED:-0}" != 1 ]]; then
  main "$@"
fi
