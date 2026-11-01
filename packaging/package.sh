#!/usr/bin/env bash
# Usage: [AOT=true] packaging/package.sh <rid> <version>   (rid: win-x64 | linux-x64 | osx-arm64 | osx-x64). Native AOT is opt-in: the MCP SDK and Avalonia use reflection, validate before enabling.
set -euo pipefail
RID="${1:?rid}"; VERSION="${2:?version}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; OUT="$ROOT/artifacts/$RID"
# The macOS packages are native for their architecture: an osx-arm64 build runs on an arm64 dotnet, never an x64 one under Rosetta.
if [[ "$RID" == osx-arm64 && "$(uname -s)" == Darwin ]]; then
  if [[ "$(uname -m)" != arm64 || "$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)" == 1 ]]; then
    echo "error: osx-arm64 must be packaged natively on Apple silicon (this shell is Intel or runs under Rosetta)" >&2; exit 1
  fi
  if dotnet --info | grep -qE '^[[:space:]]*RID:[[:space:]]+osx-x64'; then
    echo "error: the dotnet SDK on PATH is x64; install the arm64 SDK" >&2; exit 1
  fi
fi
rm -rf "$OUT"; mkdir -p "$OUT"
dotnet publish "$ROOT/src/ImmoCad.Desktop" -c Release -r "$RID" --self-contained true \
  -p:PublishAot=${AOT:-false} -p:Version="$VERSION" -o "$OUT/publish"
find "$OUT/publish" \( -name "*.pdb" -o -name "*.dSYM" -o -name "*.dbg" \) -prune -exec rm -rf {} + 2>/dev/null || true
cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES.txt" "$OUT/publish/"
cp "$ROOT/packaging/icon/icon-256.png" "$OUT/publish/immocad.png"
case "$RID" in
  win-*)   powershell.exe -NoProfile -Command "Compress-Archive -Path '$(cygpath -w "$OUT/publish")\\*' -DestinationPath '$(cygpath -w "$OUT")\\ImmoCad-$VERSION-$RID.zip' -Force" ;;
  linux-*)
    cp "$ROOT/packaging/immocad.desktop" "$OUT/publish/"
    tar -C "$OUT/publish" -czf "$OUT/ImmoCad-$VERSION-$RID.tar.gz" . ;;
  osx-*)
    APP="$OUT/ImmoCad.app"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    if command -v iconutil >/dev/null; then   # .icns from the generated PNG sizes
      SET="$OUT/icon.iconset"; mkdir -p "$SET"
      for s in 16 32 128 256 512; do cp "$ROOT/packaging/icon/icon-$s.png" "$SET/icon_${s}x${s}.png"; done
      cp "$ROOT/packaging/icon/icon-32.png" "$SET/icon_16x16@2x.png"; cp "$ROOT/packaging/icon/icon-64.png" "$SET/icon_32x32@2x.png"; cp "$ROOT/packaging/icon/icon-256.png" "$SET/icon_128x128@2x.png"; cp "$ROOT/packaging/icon/icon-512.png" "$SET/icon_256x256@2x.png"
      iconutil -c icns "$SET" -o "$APP/Contents/Resources/icon.icns"
    fi
    case "$RID" in osx-arm64) ARCH=arm64 ;; osx-x64) ARCH=x86_64 ;; *) echo "unknown macOS runtime $RID" >&2; exit 1 ;; esac
    # Only the arm64 package states an architecture priority (there is no Rosetta option, ever); the Intel package is Intel by construction.
    ARCH_PRIORITY=""; [[ "$ARCH" == arm64 ]] && ARCH_PRIORITY='<key>LSArchitecturePriority</key><array><string>arm64</string></array>'
    cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
    # NuGet's native libraries (libAvaloniaNative, libSkiaSharp, libHarfBuzzSharp) are universal binaries: keep only this package's architecture
    # (before signing: lipo invalidates signatures).
    "$ROOT/packaging/thin-macho.sh" "$APP" "$ARCH"
    sed -e "s/@VERSION@/$VERSION/g" -e "s|@ARCH_PRIORITY@|$ARCH_PRIORITY|" "$ROOT/packaging/Info.plist" > "$APP/Contents/Info.plist"
    chmod +x "$APP/Contents/MacOS/ImmoCad"
    # Apple Silicon refuses to run unsigned code: an ad-hoc signature is enough (the app is not notarized, see Read Me First).
    if command -v codesign >/dev/null; then
      codesign --force --deep --sign - "$APP"
      codesign --verify --deep --strict "$APP"
    fi
    "$ROOT/packaging/check-macho-archs.sh" "$APP" "$ARCH"
    (cd "$OUT" && ditto -c -k --keepParent ImmoCad.app "ImmoCad-$VERSION-$RID-app.zip")
    # Disk image: the app, a shortcut to Applications (drag and drop install) and the first-launch instructions.
    # hdiutil is flaky on CI runners ("resource busy"): retry, then ship the zip only.
    made=0
    if command -v hdiutil >/dev/null; then
      DMG="$OUT/dmg-$RID"; rm -rf "$DMG"; mkdir -p "$DMG"
      ditto "$APP" "$DMG/ImmoCad.app"
      ln -s /Applications "$DMG/Applications"
      cp "$ROOT/packaging/macos/ReadMeFirst.txt" "$DMG/Read Me First.txt"
      for try in 1 2 3; do
        if hdiutil create -quiet -volname ImmoCad -srcfolder "$DMG" -ov -format UDZO "$OUT/ImmoCad-$VERSION-$RID.dmg"; then made=1; break; fi
        sleep 5
      done
      rm -rf "$DMG"
    fi
    [ "$made" = "1" ] || echo "dmg creation failed; the zip is shipped on its own" ;;
esac
echo "Packaged $OUT"
