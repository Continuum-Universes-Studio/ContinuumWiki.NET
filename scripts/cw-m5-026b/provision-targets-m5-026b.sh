#!/usr/bin/env bash
set -Eeuo pipefail
exec python3 "$(dirname "$0")/provision_targets.py" "$@"
