#!/usr/bin/env bash
set -Eeuo pipefail
exec python3 "$(dirname "$0")/stage.py" --stage restore "$@"
