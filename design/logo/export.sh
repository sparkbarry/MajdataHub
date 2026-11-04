#!/usr/bin/env bash
# Exports the RhythmHub app icon (app-icon.svg) to every format the repository ships.
# Needs macOS (iconutil), Google Chrome (SVG rendering with transparency) and ImageMagick.
# Usage: design/logo/export.sh
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
src="$here/app-icon.svg"
chrome=${CHROME:-"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"}
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# render <svg> <png> <px>: rasterises at 1024 and downsamples, which keeps small sizes crisp.
render() {
  "$chrome" --headless=new --disable-gpu --hide-scrollbars --default-background-color=00000000 \
    --window-size=1024,1024 --screenshot="$tmp/full.png" "file://$1" >/dev/null 2>&1
  magick "$tmp/full.png" -filter Lanczos -resize "$3x$3" "$2"
}

# macOS: the source as drawn, an 824 px squircle on the 1024 px Apple icon grid.
iconset="$tmp/RhythmHub.iconset"
mkdir -p "$iconset"
for s in 16 32 128 256 512; do
  render "$src" "$iconset/icon_${s}x${s}.png" "$s"
  render "$src" "$iconset/icon_${s}x${s}@2x.png" $((s * 2))
done
iconutil -c icns "$iconset" -o "$root/build/macos/RhythmHub.icns"

# Windows and the web: crop the grid margin so the tile fills the canvas.
sed 's/viewBox="0 0 1024 1024"/viewBox="96 96 832 832"/' "$src" > "$tmp/tight.svg"
for s in 16 24 32 48 64 128 256; do render "$tmp/tight.svg" "$tmp/win-$s.png" "$s"; done
magick "$tmp"/win-{16,24,32,48,64,128,256}.png "$root/src/RhythmHub.Launcher/Assets/RhythmHub.ico"
magick "$tmp"/win-{16,32,48}.png "$root/site/favicon.ico"
cp "$tmp/tight.svg" "$root/site/favicon.svg"

# Apple touch icon: full-bleed square, iOS applies its own mask.
perl -0pe 's/<path d="M924[^"]*"([^>]*?)stroke="#000000" stroke-width="2"\/>/<rect width="1024" height="1024"$1\/>/' "$src" \
  | sed 's/scale(.92)/scale(1.05)/' > "$tmp/touch.svg"
render "$tmp/touch.svg" "$root/site/apple-touch-icon.png" 180

# README lockup (icon + wordmark), light and dark text, at 2x.
for t in light dark; do
  "$chrome" --headless=new --disable-gpu --hide-scrollbars --default-background-color=00000000 \
    --force-device-scale-factor=2 --window-size=900,200 --screenshot="$tmp/lockup.png" "file://$here/lockup.html#$t" >/dev/null 2>&1
  magick "$tmp/lockup.png" -trim +repage -bordercolor none -border 16 "$here/lockup-$t.png"
done

echo "Exported icns, ico, favicon, apple-touch-icon and the README lockup."
