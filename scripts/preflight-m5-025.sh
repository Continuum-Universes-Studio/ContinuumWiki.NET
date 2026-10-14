#!/usr/bin/env bash
set -Eeuo pipefail
exec python3 "$(dirname "$0")/preflight-m5-025.py" "$@"
