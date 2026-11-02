#!/usr/bin/env bash
# Uploads one platform's Velopack output (vpk pack) to the launcher's self-update feed.
#
#   launcher-upload.sh <releases-dir> <s3-prefix> <version> <cdn-url>
#
# Order matters as in go-live.sh: packages first, then installers, and the feed file
# releases.<channel>.json last, so a client never sees a release whose package is missing.
# Packages are immutable; installers, portable zips and feed files keep fixed names and are no-cache.
set -euo pipefail

dir=$1 prefix=$2 version=$3 cdn=$4
immutable="public, max-age=31536000, immutable"
mutable="no-cache"

put() { # file cache-control
  aws s3 cp "$1" "$prefix/$(basename "$1")" --cache-control "$2" --only-show-errors
  echo "  uploaded $(basename "$1")"
}

shopt -s nullglob
feeds=()
for f in "$dir"/*; do
  name=$(basename "$f")
  case "$name" in
    *.nupkg)
      # Older packages come from the feed itself (downloaded for delta generation).
      key="${prefix#s3://*/}/$name"
      bucket="${prefix#s3://}"; bucket="${bucket%%/*}"
      if aws s3api head-object --bucket "$bucket" --key "$key" >/dev/null 2>&1; then
        if [[ "$name" == *"-$version-"* ]]; then
          echo "::error::$name is already published; published versions are immutable"
          exit 1
        fi
        continue
      fi
      put "$f" "$immutable"
      ;;
    releases.*.json) feeds+=("$f") ;;
  esac
done
for f in "$dir"/*; do
  case "$(basename "$f")" in
    *.nupkg|releases.*.json) ;;
    *) put "$f" "$mutable" ;;
  esac
done
for f in "${feeds[@]}"; do
  put "$f" "$mutable"
done

for f in "${feeds[@]}"; do
  url="$cdn/$(basename "$f")"
  for _ in $(seq 1 30); do
    if curl -fsS "$url" | grep -q "\"Version\": *\"$version\""; then
      echo "  $url lists $version"
      continue 2
    fi
    sleep 5
  done
  echo "::error::$url does not list $version"
  exit 1
done
