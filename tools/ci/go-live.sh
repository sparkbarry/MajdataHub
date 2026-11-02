#!/usr/bin/env bash
# Makes a signed release live on a channel (docs/update-protocol.md §5). Used by the upload-build action
# and the "Publish game release" workflow.
#
# Expects in $DIR (the local copy of <game>/<platform>/): the signed <version>.json and <channel>.json
# written by `rhythmhub-release publish`. Order matters: the version manifest must be reachable through
# the CDN before the channel pointer is switched, and the old version is only pruned after the switch.
#
# Env: BUCKET CDN GAME PLATFORM VERSION CHANNEL DIR
#      MANIFEST_ALREADY_PUBLISHED=true  skip uploading <version>.json (it is already in the bucket)
#      PRUNE_PREVIOUS=true              delete the version this channel pointed at before, unless another
#                                       channel still points at it (used for nightly)
set -euo pipefail

# Portable across GitHub-hosted Linux/macOS and self-hosted Macs: shasum and python3 instead of sha256sum and jq.
sha256() { shasum -a 256 | cut -d' ' -f1; }

base="s3://$BUCKET/$GAME/$PLATFORM"

# wait_for <url> <file>: CloudFront must serve exactly these bytes before moving on.
wait_for() {
  local want got
  want=$(sha256 < "$2")
  for _ in $(seq 1 30); do
    got=$(curl -fsS "$1" | sha256) || true
    [[ "$got" == "$want" ]] && return 0
    sleep 2
  done
  echo "::error::$1 is not served correctly through the CDN."
  return 1
}

pointer_version() { # <s3 key> -> version it points at, or nothing
  aws s3 cp "s3://$BUCKET/$1" - 2>/dev/null |
    python3 -c 'import json, sys; print(json.load(sys.stdin).get("version", ""))' 2>/dev/null || true
}

previous=$(pointer_version "$GAME/$PLATFORM/$CHANNEL.json")

if [[ "${MANIFEST_ALREADY_PUBLISHED:-false}" != "true" ]]; then
  aws s3 cp "$DIR/$VERSION.json" "$base/$VERSION.json" --only-show-errors \
    --cache-control "public, max-age=31536000, immutable" --content-type application/json
fi
wait_for "$CDN/$GAME/$PLATFORM/$VERSION.json" "$DIR/$VERSION.json"

# The commit point for players: from here on launchers on $CHANNEL see $VERSION.
aws s3 cp "$DIR/$CHANNEL.json" "$base/$CHANNEL.json" --only-show-errors \
  --cache-control "no-cache" --content-type application/json
wait_for "$CDN/$GAME/$PLATFORM/$CHANNEL.json" "$DIR/$CHANNEL.json"

{
  echo "### $GAME $PLATFORM $VERSION is live on $CHANNEL"
  [[ -n "$previous" && "$previous" != "$VERSION" ]] && echo "Previously: $previous"
  echo "Channel: $CDN/$GAME/$PLATFORM/$CHANNEL.json"
} >> "${GITHUB_STEP_SUMMARY:-/dev/null}"

if [[ "${PRUNE_PREVIOUS:-false}" == "true" && -n "$previous" && "$previous" != "$VERSION" ]]; then
  # Channel files start with a letter, version manifests with a digit.
  in_use=false
  for key in $(aws s3api list-objects-v2 --bucket "$BUCKET" --prefix "$GAME/$PLATFORM/" --delimiter / \
                 --query 'Contents[].Key' --output text); do
    name=${key##*/}
    [[ "$name" =~ ^[A-Za-z][A-Za-z0-9_-]*\.json$ && "$name" != "$CHANNEL.json" ]] || continue
    if [[ "$(pointer_version "$key")" == "$previous" ]]; then
      in_use=true
      echo "Keeping $previous: ${name%.json} still points at it."
    fi
  done
  if [[ "$in_use" == "false" ]]; then
    echo "Pruning $GAME/$PLATFORM/$previous"
    aws s3 rm "$base/$previous/" --recursive --only-show-errors
    aws s3 rm "$base/$previous.json" --only-show-errors || true
    aws s3 rm "$base/$previous.unsigned.json" --only-show-errors || true
    echo "Removed previous $CHANNEL build $previous." >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
  fi
fi
