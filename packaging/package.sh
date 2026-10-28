#!/usr/bin/env bash
# Usage: [AOT=true] packaging/package.sh <rid> <version>   (rid: win-x64 | linux-x64 | osx-arm64 | osx-x64). Native AOT is opt-in: the MCP SDK and Avalonia use reflection, validate before enabling.
set -euo pipefail
RID="${1:?rid}"; VERSION="${2:?version}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; OUT="$ROOT/artifacts/$RID"; rm -rf "$OUT"; mkdir -p "$OUT"
dotnet publish "$ROOT/src/ImmoCad.Desktop" -c Release -r "$RID" --self-contained true \
  -p:PublishAot=${AOT:-false} -p:Version="$VERSION" -o "$OUT/publish"
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
    cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
    sed "s/@VERSION@/$VERSION/g" "$ROOT/packaging/Info.plist" > "$APP/Contents/Info.plist"
    (cd "$OUT" && ditto -c -k --keepParent ImmoCad.app "ImmoCad-$VERSION-$RID-app.zip")
    hdiutil create -quiet -volname ImmoCad -srcfolder "$APP" -ov -format UDZO "$OUT/ImmoCad-$VERSION-$RID.dmg" ;;
esac
echo "Packaged $OUT"
