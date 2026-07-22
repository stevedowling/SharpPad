#!/usr/bin/env bash
# Build SharpPad Linux packages (.deb, .rpm, .tar.gz) for one runtime identifier.
#
#   packaging/build-linux-packages.sh <rid> <version> [outdir]
#
# Example: packaging/build-linux-packages.sh linux-x64 1.2.0 dist
#
# Requires: dotnet SDK 10, fpm (for deb/rpm), tar. rpm generation additionally
# needs the `rpmbuild`/`rpm` tooling that fpm shells out to.
set -euo pipefail

RID="${1:?usage: build-linux-packages.sh <rid> <version> [outdir]}"
VERSION="${2:?version required, e.g. 1.2.0}"
OUTDIR="${3:-dist}"

# Repo root = parent of this script's directory.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

NAME="sharppad"
APP_EXE="SharpPad.App"          # published native launcher
INSTALL_LIB="/usr/lib/${NAME}"  # self-contained app lives here

# Map .NET RID -> (deb arch, rpm arch, tarball arch label).
case "$RID" in
  linux-x64)   DEB_ARCH=amd64 ; RPM_ARCH=x86_64  ; TAR_ARCH=x64   ;;
  linux-arm64) DEB_ARCH=arm64 ; RPM_ARCH=aarch64 ; TAR_ARCH=arm64 ;;
  *) echo "unsupported RID: $RID" >&2; exit 2 ;;
esac

echo ">> publishing $APP_EXE for $RID (self-contained)"
PUBDIR="$(mktemp -d)"
dotnet publish "$ROOT/src/SharpPad.App" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishTrimmed=false \
  -o "$PUBDIR" --nologo -v:q

# Normalise permissions once, upstream of every packaging step: the build host's
# umask can leave published files mode 600, which becomes root:root 600 after a
# system install and is then unreadable by the user launching the app.
find "$PUBDIR" -type d -exec chmod 755 {} +
find "$PUBDIR" -type f -exec chmod 644 {} +
chmod 755 "$PUBDIR/$APP_EXE"
find "$PUBDIR" -type f -name '*.so' -exec chmod 755 {} +

# ---- stage an FHS tree the packagers can consume verbatim ----
echo ">> staging package tree"
PKGROOT="$(mktemp -d)"
mkdir -p "$PKGROOT$INSTALL_LIB" \
         "$PKGROOT/usr/bin" \
         "$PKGROOT/usr/share/applications" \
         "$PKGROOT/usr/share/icons/hicolor/scalable/apps" \
         "$PKGROOT/usr/share/doc/$NAME"

cp -a "$PUBDIR/." "$PKGROOT$INSTALL_LIB/"
ln -s "$INSTALL_LIB/$APP_EXE" "$PKGROOT/usr/bin/$NAME"
install -m644 "$ROOT/packaging/sharppad.desktop" "$PKGROOT/usr/share/applications/$NAME.desktop"
install -m644 "$ROOT/packaging/sharppad.svg"     "$PKGROOT/usr/share/icons/hicolor/scalable/apps/$NAME.svg"
install -m644 "$ROOT/README.md"                  "$PKGROOT/usr/share/doc/$NAME/README.md"

mkdir -p "$ROOT/$OUTDIR"
OUT="$(cd "$ROOT/$OUTDIR" && pwd)"

DESC="LINQPad-style C# scratchpad for Linux. Write C#, press F5, get rich Dump() output. Requires the .NET 10 SDK for script compilation."
URL="https://github.com/stevedowling/SharpPad"
MAINT="Steve Dowling <steve@dowling.co.nz>"

fpm_common=(
  -s dir -n "$NAME" -v "$VERSION"
  --description "$DESC" --url "$URL" --maintainer "$MAINT"
  --license MIT --vendor "Steve Dowling"
  --category devel
  -C "$PKGROOT" --force
)

have() { command -v "$1" >/dev/null 2>&1; }

# ---- .deb ----
if have fpm; then
  echo ">> building .deb ($DEB_ARCH)"
  fpm "${fpm_common[@]}" -t deb -a "$DEB_ARCH" \
    --depends libc6 --depends 'libstdc++6' --depends libfontconfig1 --depends libx11-6 \
    --depends libice6 --depends libsm6 \
    --deb-recommends dotnet-sdk-10.0 \
    --deb-no-default-config-files \
    -p "$OUT/${NAME}_${VERSION}_${DEB_ARCH}.deb" \
    usr

  # ---- .rpm ---- (needs rpmbuild available)
  if have rpmbuild; then
    echo ">> building .rpm ($RPM_ARCH)"
    fpm "${fpm_common[@]}" -t rpm -a "$RPM_ARCH" \
      --depends fontconfig --depends libX11 \
      -p "$OUT/${NAME}-${VERSION}.${RPM_ARCH}.rpm" \
      usr
  else
    echo "!! rpmbuild not found; skipping .rpm" >&2
  fi
else
  echo "!! fpm not found; skipping .deb/.rpm" >&2
fi

# ---- portable tarball (Arch and everything else) ----
echo ">> building .tar.gz ($TAR_ARCH)"
TARSTAGE="$(mktemp -d)"
TARDIR="$TARSTAGE/${NAME}-${VERSION}-linux-${TAR_ARCH}"
mkdir -p "$TARDIR"
cp -a "$PUBDIR/." "$TARDIR/"
install -m644 "$ROOT/packaging/sharppad.desktop" "$TARDIR/sharppad.desktop"
install -m644 "$ROOT/packaging/sharppad.svg"     "$TARDIR/sharppad.svg"
tar -C "$TARSTAGE" -czf "$OUT/${NAME}-${VERSION}-linux-${TAR_ARCH}.tar.gz" "$(basename "$TARDIR")"

rm -rf "$PUBDIR" "$PKGROOT" "$TARSTAGE"
echo ">> done. artifacts in $OUT:"
ls -1 "$OUT"
