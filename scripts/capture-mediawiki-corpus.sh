#!/usr/bin/env bash
set -Eeuo pipefail

corpus=${1:?usage: $0 CORPUS.json OUTPUT_DIR}
output_dir=${2:?usage: $0 CORPUS.json OUTPUT_DIR}
: "${CW_MEDIAWIKI_API_URL:?set approved HTTPS MediaWiki Action API URL}"
: "${CW_MEDIAWIKI_APPROVED_ORIGIN:?set exact approved HTTPS origin}"
mkdir -p -- "$output_dir"

while IFS=$'\t' read -r fixture page revision invocation; do
  python3 scripts/capture-mediawiki-reference.py \
    --api-url "$CW_MEDIAWIKI_API_URL" \
    --approved-origin "$CW_MEDIAWIKI_APPROVED_ORIGIN" \
    --page "$page" \
    --oldid "$revision" \
    --invocation "$invocation" \
    --fixture-id "$fixture" \
    --output "$output_dir/$fixture.json"
done < <(jq -r '.Cases[] | [.FixtureId, .PageTitle, .SourceRevisionId, .RawInvocation] | @tsv' "$corpus")

printf 'Captured %s reference fixtures under %s\n' "$(find "$output_dir" -maxdepth 1 -type f -name '*.json' | wc -l)" "$output_dir"
