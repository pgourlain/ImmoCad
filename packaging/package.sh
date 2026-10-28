#!/usr/bin/env bash
# Usage: packaging/package.sh <rid> <version>   (rid: win-x64 | linux-x64 | osx-arm64 | osx-x64)
set -euo pipefail
RID="${1:?rid}"; VERSION="${2:?version}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; OUT="$ROOT/artifacts/$RID"; rm -rf "$OUT"; mkdir -p "$OUT"
dotnet publish "$ROOT/src/ImmeubleCad.Desktop" -c Release -r "$RID" --self-contained true \
  -p:PublishAot=true -p:Version="$VERSION" -o "$OUT/publish"
cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES.txt" "$OUT/publish/"
case "$RID" in
  win-*)   (cd "$OUT/publish" && zip -qr "../ImmeubleCad-$VERSION-$RID.zip" .) ;;
  linux-*)
    cp "$ROOT/packaging/immeublecad.desktop" "$OUT/publish/"
    tar -C "$OUT/publish" -czf "$OUT/ImmeubleCad-$VERSION-$RID.tar.gz" . ;;
  osx-*)
    APP="$OUT/ImmeubleCad.app"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
    sed "s/@VERSION@/$VERSION/g" "$ROOT/packaging/Info.plist" > "$APP/Contents/Info.plist"
    hdiutil create -quiet -volname ImmeubleCad -srcfolder "$APP" -ov -format UDZO "$OUT/ImmeubleCad-$VERSION-$RID.dmg" ;;
esac
echo "Packaged $OUT"
