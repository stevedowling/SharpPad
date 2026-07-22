#!/usr/bin/env bash
#
# SharpPad installer. Detects your Linux distribution and CPU architecture,
# downloads the matching package from the latest GitHub release, and installs it.
#
#   curl -fsSL https://raw.githubusercontent.com/stevedowling/SharpPad/main/install.sh | bash
#
# Options (environment variables):
#   SHARPPAD_VERSION=1.2.0   install a specific version instead of the latest
#   SHARPPAD_METHOD=tarball  force install method: deb | rpm | tarball
#
#   ./install.sh --uninstall   remove a previously installed SharpPad
#
set -euo pipefail

REPO="stevedowling/SharpPad"
NAME="sharppad"

# ---- pretty output -------------------------------------------------------
info()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn()  { printf '\033[1;33m!!\033[0m %s\n' "$*" >&2; }
die()   { printf '\033[1;31mError:\033[0m %s\n' "$*" >&2; exit 1; }

# ---- privilege helper ----------------------------------------------------
if [ "$(id -u)" -eq 0 ]; then SUDO=""; else SUDO="sudo"; fi
run_root() {
  if [ -n "$SUDO" ] && ! command -v sudo >/dev/null 2>&1; then
    die "This step needs root. Re-run as root or install sudo."
  fi
  $SUDO "$@"
}

need() { command -v "$1" >/dev/null 2>&1; }

# ---- architecture --------------------------------------------------------
detect_arch() {
  case "$(uname -m)" in
    x86_64|amd64)  DEB_ARCH=amd64; RPM_ARCH=x86_64;  TAR_ARCH=x64   ;;
    aarch64|arm64) DEB_ARCH=arm64; RPM_ARCH=aarch64; TAR_ARCH=arm64 ;;
    *) die "Unsupported architecture: $(uname -m). SharpPad ships x64 and arm64 only." ;;
  esac
}

# ---- distribution / package manager -------------------------------------
# Sets METHOD (deb|rpm|tarball) and DISTRO_ID unless SHARPPAD_METHOD forces it.
detect_distro() {
  DISTRO_ID="unknown"; DISTRO_LIKE=""
  if [ -r /etc/os-release ]; then
    # shellcheck disable=SC1091
    . /etc/os-release
    DISTRO_ID="${ID:-unknown}"
    DISTRO_LIKE="${ID_LIKE:-}"
  fi

  if [ -n "${SHARPPAD_METHOD:-}" ]; then
    METHOD="$SHARPPAD_METHOD"
    return
  fi

  case "$DISTRO_ID $DISTRO_LIKE" in
    *debian*|*ubuntu*|*mint*|*pop*)                 METHOD=deb ;;
    *fedora*|*rhel*|*centos*|*rocky*|*alma*|*suse*) METHOD=rpm ;;
    *arch*|*manjaro*|*endeavour*)                   METHOD=tarball ;;
    *)
      # Fall back to whatever package manager is actually present.
      if   need apt-get || need dpkg; then METHOD=deb
      elif need dnf || need yum || need zypper || need rpm; then METHOD=rpm
      else METHOD=tarball
      fi
      ;;
  esac
}

# ---- release metadata (no jq dependency) --------------------------------
resolve_release() {
  local api
  if [ -n "${SHARPPAD_VERSION:-}" ]; then
    TAG="v${SHARPPAD_VERSION#v}"
    api="https://api.github.com/repos/$REPO/releases/tags/$TAG"
  else
    api="https://api.github.com/repos/$REPO/releases/latest"
  fi

  info "Querying release metadata..."
  RELEASE_JSON="$(curl -fsSL -H 'Accept: application/vnd.github+json' "$api")" \
    || die "Could not fetch release info from GitHub. Check the version or your network."

  TAG="$(printf '%s' "$RELEASE_JSON" | grep -m1 '"tag_name"' | sed -E 's/.*"tag_name": *"([^"]+)".*/\1/')"
  VERSION="${TAG#v}"
  [ -n "$VERSION" ] || die "Could not determine version from the release."
}

# Pick the asset URL whose filename contains all given substrings.
asset_url() {
  local url
  url="$(printf '%s' "$RELEASE_JSON" \
    | grep -o '"browser_download_url": *"[^"]*"' \
    | sed -E 's/.*"(https[^"]+)".*/\1/' \
    | while read -r u; do
        local ok=1
        for pat in "$@"; do case "$u" in *"$pat"*) ;; *) ok=0 ;; esac; done
        [ "$ok" -eq 1 ] && echo "$u"
      done | head -n1)"
  [ -n "$url" ] || return 1
  printf '%s\n' "$url"
}

download() {
  local url="$1" dest="$2"
  info "Downloading $(basename "$url")"
  curl -fSL --progress-bar "$url" -o "$dest" || die "Download failed: $url"
}

# ---- install methods -----------------------------------------------------
install_deb() {
  local url file
  url="$(asset_url "_${DEB_ARCH}.deb")" \
    || die "No .deb for $DEB_ARCH in release $TAG."
  file="$TMP/${NAME}.deb"
  download "$url" "$file"
  info "Installing with apt/dpkg..."
  if need apt-get; then
    run_root apt-get install -y "$file" || run_root apt-get install -y -f "$file"
  else
    run_root dpkg -i "$file" || { run_root apt-get -f install -y 2>/dev/null || true; }
  fi
}

install_rpm() {
  local url file
  url="$(asset_url ".${RPM_ARCH}.rpm")" \
    || die "No .rpm for $RPM_ARCH in release $TAG."
  file="$TMP/${NAME}.rpm"
  download "$url" "$file"
  info "Installing with dnf/zypper/rpm..."
  if   need dnf;    then run_root dnf install -y "$file"
  elif need zypper; then run_root zypper --non-interactive install --allow-unsigned-rpm "$file"
  elif need yum;    then run_root yum install -y "$file"
  else                   run_root rpm -Uvh "$file"
  fi
}

install_tarball() {
  local url file
  url="$(asset_url "linux-${TAR_ARCH}.tar.gz")" \
    || die "No tarball for $TAR_ARCH in release $TAG."
  file="$TMP/${NAME}.tar.gz"
  download "$url" "$file"

  info "Installing to /opt/${NAME}..."
  local extract="$TMP/extract"
  mkdir -p "$extract"
  tar -C "$extract" -xzf "$file"
  local src
  src="$(find "$extract" -maxdepth 1 -type d -name "${NAME}-*" | head -n1)"
  [ -n "$src" ] || die "Unexpected tarball layout."

  run_root rm -rf "/opt/${NAME}"
  run_root mkdir -p "/opt/${NAME}"
  run_root cp -a "$src/." "/opt/${NAME}/"
  run_root chmod 755 "/opt/${NAME}/SharpPad.App"
  run_root ln -sf "/opt/${NAME}/SharpPad.App" "/usr/local/bin/${NAME}"

  if [ -f "/opt/${NAME}/sharppad.desktop" ]; then
    run_root install -Dm644 "/opt/${NAME}/sharppad.desktop" "/usr/share/applications/${NAME}.desktop"
    run_root sed -i 's#^Exec=.*#Exec=/usr/local/bin/sharppad %F#' "/usr/share/applications/${NAME}.desktop"
  fi
  if [ -f "/opt/${NAME}/sharppad.svg" ]; then
    run_root install -Dm644 "/opt/${NAME}/sharppad.svg" \
      "/usr/share/icons/hicolor/scalable/apps/${NAME}.svg"
  fi
}

# ---- .NET SDK check ------------------------------------------------------
check_dotnet() {
  if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    info ".NET 10 SDK detected."
    return
  fi
  warn "The .NET 10 SDK was not found. SharpPad runs, but compiling scripts needs it."
  case "$METHOD" in
    deb) warn "Install it with:  sudo apt-get install -y dotnet-sdk-10.0" ;;
    rpm) warn "Install it with:  sudo dnf install -y dotnet-sdk-10.0   (or zypper)" ;;
    *)   warn "Install it from:  https://dotnet.microsoft.com/download/dotnet/10.0" ;;
  esac
}

# ---- uninstall -----------------------------------------------------------
uninstall() {
  info "Uninstalling SharpPad..."
  if need dpkg && dpkg -s "$NAME" >/dev/null 2>&1; then
    run_root apt-get remove -y "$NAME" 2>/dev/null || run_root dpkg -r "$NAME"
  elif need rpm && rpm -q "$NAME" >/dev/null 2>&1; then
    if   need dnf; then run_root dnf remove -y "$NAME"
    elif need zypper; then run_root zypper --non-interactive remove "$NAME"
    else run_root rpm -e "$NAME"; fi
  fi
  # tarball install cleanup
  run_root rm -rf "/opt/${NAME}"
  run_root rm -f "/usr/local/bin/${NAME}" \
                 "/usr/share/applications/${NAME}.desktop" \
                 "/usr/share/icons/hicolor/scalable/apps/${NAME}.svg"
  info "Done."
}

# ---- main ----------------------------------------------------------------
main() {
  need curl || die "curl is required."
  need tar  || die "tar is required."

  if [ "${1:-}" = "--uninstall" ]; then
    uninstall
    exit 0
  fi

  detect_arch
  detect_distro
  info "Detected: distro=${DISTRO_ID} arch=$(uname -m) -> method=${METHOD}"

  TMP="$(mktemp -d)"
  trap 'rm -rf "$TMP"' EXIT

  resolve_release
  info "Installing SharpPad ${VERSION}"

  case "$METHOD" in
    deb)     install_deb ;;
    rpm)     install_rpm ;;
    tarball) install_tarball ;;
    *) die "Unknown install method: $METHOD" ;;
  esac

  check_dotnet
  info "SharpPad ${VERSION} installed. Launch it from your app menu or run: ${NAME}"
}

main "$@"
