#!/usr/bin/env bash
set -Eeuo pipefail

# CW-M3-006C disposable real-module import and compatibility workflow.
# This harness never targets a production database and never starts an
# unconfined Lua fallback. Socket-backed rendering is enabled only when the
# caller supplies CW_SCRIBUNTO_SOCKET_PATH.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
container_name="cw-m3-006c-verify"
database_name="cw_m3_003a"
port="${CW_M3_006C_PORT:-3326}"
password="cw-m3-006c-disposable"
work="$(mktemp -d "${TMPDIR:-/tmp}/cw-m3-006c.XXXXXX")"

cleanup() {
    status=$?
    set +e
    podman rm -f "$container_name" >/dev/null 2>&1 || true
    rm -rf "$work"
    exit "$status"
}
trap cleanup EXIT INT TERM

podman rm -f "$container_name" >/dev/null 2>&1 || true
podman run -d --name "$container_name" \
  -e MARIADB_ROOT_PASSWORD="$password" \
  -e MARIADB_DATABASE="$database_name" \
  -p "${port}:3306" docker.io/library/mariadb:11.8 \
  --character-set-server=utf8mb4 --collation-server=utf8mb4_unicode_ci >/dev/null
for _ in $(seq 1 60); do
    podman exec "$container_name" healthcheck.sh --connect --innodb_initialized >/dev/null 2>&1 && break
    sleep 1
done
podman exec "$container_name" healthcheck.sh --connect --innodb_initialized >/dev/null

fixture="$repo_root/MediaWikiInventory/Fixtures/scribunto-moonsharp-compatibility-fixture.xml"
dotnet run --project "$repo_root/MediaWikiInventory/MediaWikiInventory.csproj" -- modules \
  --input "$fixture" --output "$repo_root/artifacts/validation/mediawiki-module-inventory-v1.json"
dotnet run --project "$repo_root/MediaWikiInventory/MediaWikiInventory.csproj" -- compatibility \
  --input "$fixture" --output "$repo_root/artifacts/validation/mediawiki-module-compatibility-v1.json"

real_export="$repo_root/artifacts/imports/mediawiki/continuumwiki-full-with-files.xml"
real_subset="$work/continuumwiki-selected-compatibility.xml"
real_plan="$work/continuumwiki-selected-plan.json"
real_fixture_report="$repo_root/artifacts/validation/mediawiki-real-fixture-blockers-v1.json"
real_report="$repo_root/artifacts/validation/mediawiki-real-module-import-v1.json"
diagnostic_mode="${CW_M3_006C_DIAGNOSTIC:-0}"
import_policy_args=()
if [[ "$diagnostic_mode" == 1 ]]; then
    import_policy_args=(--diagnostic-policy disposable-rendering)
fi
dotnet run --project "$repo_root/MediaWikiInventory/MediaWikiInventory.csproj" -- modules \
  --input "$real_export" --output "$repo_root/artifacts/validation/mediawiki-module-inventory-v1.json"
dotnet run --project "$repo_root/MediaWikiInventory/MediaWikiInventory.csproj" -- select-compatibility \
  --input "$real_export" \
  --corpus "$repo_root/artifacts/validation/mediawiki-compatibility-corpus-v1.json" \
  --output "$real_subset"
plan_status=0
set +e
dotnet run --project "$repo_root/MediaWikiInventory/MediaWikiInventory.csproj" -- plan \
  --input "$real_subset" --output "$real_plan"
plan_status=$?
set -e
python3 "$repo_root/scripts/write-m3-006c-fixture-report.py" \
  "$repo_root/artifacts/validation/mediawiki-compatibility-corpus-v1.json" \
  "$real_plan" "$real_fixture_report"
if [[ "$plan_status" -ne 0 ]]; then
    if [[ "$diagnostic_mode" != 1 ]]; then
        echo "CW-M3-006C: compatibility import plan rejected with status $plan_status; see $real_fixture_report." >&2
        exit "$plan_status"
    fi
    diagnostic_corpus="$work/continuumwiki-diagnostic-corpus.json"
    diagnostic_subset="$work/continuumwiki-diagnostic-selected.xml"
    diagnostic_plan="$work/continuumwiki-diagnostic-plan.json"
    jq --slurpfile evaluation "$repo_root/artifacts/validation/mediawiki-reference-aware-evaluation-v1.json" \
      '. + {Cases: [.Cases[] as $case | select(any($evaluation[0].Evaluations[]; .FixtureId == $case.FixtureId and .ReferenceBehavior == "REFERENCE_SUCCESS")) | $case]}' \
      "$repo_root/artifacts/validation/mediawiki-compatibility-corpus-v1.json" > "$diagnostic_corpus"
    dotnet run --project "$repo_root/MediaWikiInventory/MediaWikiInventory.csproj" -- select-compatibility \
      --input "$real_export" --corpus "$diagnostic_corpus" --output "$diagnostic_subset"
    diagnostic_plan_status=0
    set +e
    dotnet run --project "$repo_root/MediaWikiInventory/MediaWikiInventory.csproj" -- plan \
      --input "$diagnostic_subset" --output "$diagnostic_plan"
    diagnostic_plan_status=$?
    set -e
    diagnostic_non_allowlisted="$(jq '[.Findings[] | select(.Severity == "Blocking") | select((.Code != "UnsupportedExtensionTag") or (((.Message | contains("<templatedata>")) or (.Message | contains("<mermaid>")) or (.Message | contains("<templatestyles>"))) | not))] | length' "$diagnostic_plan")"
    if [[ "$diagnostic_non_allowlisted" -ne 0 ]]; then
        echo "CW-M3-006C: diagnostic plan still has non-audited blockers; no import attempted." >&2
        exit "$diagnostic_plan_status"
    fi
    real_subset="$diagnostic_subset"
    real_plan="$diagnostic_plan"
fi

connection="Server=127.0.0.1;Port=${port};Database=${database_name};User ID=root;Password=${password};"
export CONTINUUMWIKI_RUN_MYSQL_TESTS=1
export CONTINUUMWIKI_MYSQL_TEST_CONNECTION="$connection"
export CONTINUUMWIKI_M3_TARGET_CONNECTION="$connection"
dotnet run --project "$repo_root/MediaWikiImport/MediaWikiImport.csproj" -- provision \
  --target-config "$repo_root/MediaWikiImport/Fixtures/disposable-target.template.json"
dotnet run --project "$repo_root/MediaWikiImport/MediaWikiImport.csproj" -- import \
  --input "$real_subset" --plan "$real_plan" \
  --target-config "$repo_root/MediaWikiImport/Fixtures/disposable-target.template.json" \
  "${import_policy_args[@]}" \
  --reconciliation-output "$real_report"
import_verdict="$(jq -r '.Verdict // empty' "$real_report")"
import_batch_id="$(jq -r '.ImportBatchId // empty' "$real_report")"
if [[ "$import_verdict" != VERIFIED ]]; then
    echo "CW-M3-006C: import report verdict was '$import_verdict', expected VERIFIED." >&2
    exit 1
fi
if [[ ! "$import_batch_id" =~ ^[0-9]+$ || "$import_batch_id" -le 0 ]]; then
    echo 'CW-M3-006C: import report did not contain a positive numeric ImportBatchId.' >&2
    exit 1
fi
active_database="$(podman exec "$container_name" mariadb --batch --skip-column-names \
  -uroot -p"$password" "$database_name" -e 'SELECT DATABASE();')"
if [[ "$active_database" != "$database_name" || "$connection" != *"Database=${database_name};"* ]]; then
    echo "CW-M3-006C: active disposable database identity '$active_database' did not match '$database_name'." >&2
    exit 1
fi
dotnet run --project "$repo_root/MediaWikiInventory/MediaWikiInventory.csproj" -- compatibility \
  --input "$real_export" --import-report "$real_report" \
  --output "$repo_root/artifacts/validation/mediawiki-module-compatibility-v1.json"
comparison_status=0
set +e
env \
  CONTINUUMWIKI_M3_006C_OUTPUT_CONNECTION="$connection" \
  CONTINUUMWIKI_M3_006C_IMPORT_BATCH_ID="$import_batch_id" \
  CW_M3_006C_REFERENCE_DIR="${HOME}/continuumwiki-validation/references" \
  CW_M3_006C_OUTPUT_DIR="${HOME}/continuumwiki-validation/continuum-outputs" \
  CW_M3_006C_COMPARISON_DIR="${HOME}/continuumwiki-validation/comparisons" \
  CW_M3_006C_CORPUS="$repo_root/artifacts/validation/mediawiki-compatibility-corpus-v1.json" \
  CW_M3_006C_FIXTURE_IDS="$(jq -r '[.Evaluations[] | select(.ReferenceBehavior == "REFERENCE_SUCCESS") | .FixtureId] | join(",")' "$repo_root/artifacts/validation/mediawiki-reference-aware-evaluation-v1.json")" \
  scripts/generate-m3-006c4-comparisons.sh
comparison_status=$?
set -e
echo "CW-M3-006C: real-reference comparison exit status=$comparison_status"
storage_test_status=0
set +e
dotnet test "$repo_root/TightWiki.Tests/TightWiki.Tests.csproj" -c Debug --no-restore -m:1 \
  --filter 'FullyQualifiedName~DisposableModuleImportResolvesHistoricalRevisionsAndUsesTheRealRenderRepository'
storage_test_status=$?
set -e
echo "CW-M3-006C: storage-backed render test exit status=$storage_test_status"

socket_test_status=0
if [[ -n "${CW_SCRIBUNTO_SOCKET_PATH:-}" ]]; then
    set +e
    dotnet test "$repo_root/TightWiki.Tests/TightWiki.Tests.csproj" -c Debug --no-restore -m:1 \
      --filter 'FullyQualifiedName~MediaWikiScribuntoSocketRendererIntegrationTests'
    socket_test_status=$?
    set -e
    echo "CW-M3-006C: socket-backed render test exit status=$socket_test_status"
else
    echo 'CW-M3-006C: socket-backed render test not evaluated; CW_SCRIBUNTO_SOCKET_PATH is unset.'
fi

echo 'CW-M3-006C disposable import and inventory workflow completed.'
if [[ "$comparison_status" -ne 0 || "$storage_test_status" -ne 0 || "$socket_test_status" -ne 0 ]]; then
    exit 1
fi
