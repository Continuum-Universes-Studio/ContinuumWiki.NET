#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
reference_dir="${CW_M3_006C_REFERENCE_DIR:-${HOME}/continuumwiki-validation/references}"
output_dir="${CW_M3_006C_OUTPUT_DIR:-${HOME}/continuumwiki-validation/continuum-outputs}"
comparison_dir="${CW_M3_006C_COMPARISON_DIR:-${HOME}/continuumwiki-validation/comparisons}"
: "${CONTINUUMWIKI_M3_006C_OUTPUT_CONNECTION:?set the disposable MariaDB connection string}"
: "${CONTINUUMWIKI_M3_006C_IMPORT_BATCH_ID:?set the verified disposable import batch ID}"
: "${CW_SCRIBUNTO_SOCKET_PATH:?set the authorized Unix-domain worker socket}"
: "${CW_M3_006C_CORPUS:?set the preserved compatibility corpus path}"
if [[ "$CONTINUUMWIKI_M3_006C_OUTPUT_CONNECTION" == *"<"* || "$CONTINUUMWIKI_M3_006C_OUTPUT_CONNECTION" == *">"* ]]; then
    echo 'CONTINUUMWIKI_M3_006C_OUTPUT_CONNECTION still contains a placeholder.' >&2
    exit 2
fi
if [[ "$CONTINUUMWIKI_M3_006C_IMPORT_BATCH_ID" == *"<"* || "$CONTINUUMWIKI_M3_006C_IMPORT_BATCH_ID" == *">"* || ! "$CONTINUUMWIKI_M3_006C_IMPORT_BATCH_ID" =~ ^[0-9]+$ ]]; then
    echo 'CONTINUUMWIKI_M3_006C_IMPORT_BATCH_ID must be the numeric ImportBatchId from the verified reconciliation report.' >&2
    exit 2
fi
test -S "$CW_SCRIBUNTO_SOCKET_PATH"
test "$(find "$reference_dir" -maxdepth 1 -type f -name 'mw-invoke-*.json' | wc -l)" -eq 8
mkdir -p -- "$output_dir" "$comparison_dir"

generation_status=0
set +e
dotnet test "$repo_root/TightWiki.Tests/TightWiki.Tests.csproj" \
  -c Debug --no-restore -m:1 \
  --filter 'FullyQualifiedName~MediaWikiReferenceOutputIntegrationTests.GenerateContinuumOutputsForCapturedRealMediaWikiReferences'
generation_status=$?
set -e

comparison_status=0
while IFS= read -r reference; do
    fixture="$(basename "$reference" .json)"
    set +e
    python3 "$repo_root/scripts/compare-mediawiki-reference.py" \
      --reference "$reference" \
      --continuum-output "$output_dir/$fixture.json" \
      --output "$comparison_dir/$fixture.json"
    status=$?
    set -e
    if [[ "$status" -ne 0 ]]; then comparison_status=1; fi
done < <(find "$reference_dir" -maxdepth 1 -type f -name 'mw-invoke-*.json' -print | sort | while IFS= read -r reference; do
    if [[ -z "${CW_M3_006C_FIXTURE_IDS:-}" ]]; then
        printf '%s\n' "$reference"
    else
        fixture="$(basename "$reference" .json)"
        case ",${CW_M3_006C_FIXTURE_IDS}," in
            *",$fixture,"*) printf '%s\n' "$reference" ;;
        esac
    fi
done)

printf 'Generated=%s Comparisons=%s\n' \
  "$(find "$output_dir" -maxdepth 1 -type f -name 'mw-invoke-*.json' | wc -l)" \
  "$(find "$comparison_dir" -maxdepth 1 -type f -name 'mw-invoke-*.json' | wc -l)"
if [[ "$generation_status" -ne 0 || "$comparison_status" -ne 0 ]]; then
    exit 1
fi
