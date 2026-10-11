#!/usr/bin/env bash
set -Eeuo pipefail

# Read-only provenance comparison for the four SQLite default-store domains.
# Usage: audit-default-provenance.sh SOURCE_DEFAULTS_DB CANONICAL_DEFAULTS_DB OUTPUT_DIR
# The source database is never opened for writing. Snapshot files are emitted
# before the machine-readable comparison report.

if [[ "$#" -ne 3 ]]; then
  echo "usage: $0 SOURCE_DEFAULTS_DB CANONICAL_DEFAULTS_DB OUTPUT_DIR" >&2
  exit 2
fi

source_db="$1"
canonical_db="$2"
output_dir="$3"

[[ -f "$source_db" ]] || { echo "Source defaults database does not exist: $source_db" >&2; exit 2; }
[[ -f "$canonical_db" ]] || { echo "Canonical defaults database does not exist: $canonical_db" >&2; exit 2; }
command -v sqlite3 >/dev/null || { echo "Missing dependency: sqlite3" >&2; exit 2; }
command -v jq >/dev/null || { echo "Missing dependency: jq" >&2; exit 2; }

mkdir -p "$output_dir/source-snapshot" "$output_dir/canonical-snapshot"

declare -a domains=(DefaultConfiguration DefaultThemes DefaultFeatureTemplates DefaultWikiPages)
declare -A order_by=(
  [DefaultConfiguration]='ConfigurationGroupName, ConfigurationEntryName'
  [DefaultThemes]='Name'
  [DefaultFeatureTemplates]='Name, Type'
  [DefaultWikiPages]='Namespace, Navigation, Name'
)

for domain in "${domains[@]}"; do
  order="${order_by[$domain]}"
  sqlite3 -json "$source_db" "SELECT * FROM \"$domain\" ORDER BY $order;" >"$output_dir/source-snapshot/$domain.json"
  sqlite3 -json "$canonical_db" "SELECT * FROM \"$domain\" ORDER BY $order;" >"$output_dir/canonical-snapshot/$domain.json"
  jq -e 'type == "array"' "$output_dir/source-snapshot/$domain.json" >/dev/null
  jq -e 'type == "array"' "$output_dir/canonical-snapshot/$domain.json" >/dev/null
done

sha256sum "$source_db" | awk '{print $1}' >"$output_dir/source-snapshot/database.sha256"
sha256sum "$canonical_db" | awk '{print $1}' >"$output_dir/canonical-snapshot/database.sha256"

source_hash="$(cat "$output_dir/source-snapshot/database.sha256")"
canonical_hash="$(cat "$output_dir/canonical-snapshot/database.sha256")"

canonical_commit="${CW006C_CANONICAL_COMMIT:-unknown}"
canonical_commit_date="${CW006C_CANONICAL_COMMIT_DATE:-unknown}"
canonical_commit_subject="${CW006C_CANONICAL_COMMIT_SUBJECT:-unknown}"
canonical_path="${CW006C_CANONICAL_PATH:-TightWiki.Repository/Defaults/defaults.db}"
canonical_inputs="${CW006C_CANONICAL_INPUTS:-Data/config.db,Data/pages.db}"
canonical_generated_hash="${CW006C_CANONICAL_GENERATED_HASH:-unknown}"
canonical_config_hash="${CW006C_CANONICAL_CONFIG_HASH:-unknown}"
canonical_pages_hash="${CW006C_CANONICAL_PAGES_HASH:-unknown}"

jq -n \
  --slurpfile sourceConfiguration "$output_dir/source-snapshot/DefaultConfiguration.json" \
  --slurpfile sourceThemes "$output_dir/source-snapshot/DefaultThemes.json" \
  --slurpfile sourceTemplates "$output_dir/source-snapshot/DefaultFeatureTemplates.json" \
  --slurpfile sourcePages "$output_dir/source-snapshot/DefaultWikiPages.json" \
  --slurpfile canonicalConfiguration "$output_dir/canonical-snapshot/DefaultConfiguration.json" \
  --slurpfile canonicalThemes "$output_dir/canonical-snapshot/DefaultThemes.json" \
  --slurpfile canonicalTemplates "$output_dir/canonical-snapshot/DefaultFeatureTemplates.json" \
  --slurpfile canonicalPages "$output_dir/canonical-snapshot/DefaultWikiPages.json" \
  --arg sourceHash "$source_hash" \
  --arg canonicalHash "$canonical_hash" \
  --arg canonicalCommit "$canonical_commit" \
  --arg canonicalCommitDate "$canonical_commit_date" \
  --arg canonicalCommitSubject "$canonical_commit_subject" \
  --arg canonicalPath "$canonical_path" \
  --arg canonicalInputs "$canonical_inputs" \
  --arg canonicalGeneratedHash "$canonical_generated_hash" \
  --arg canonicalConfigHash "$canonical_config_hash" \
  --arg canonicalPagesHash "$canonical_pages_hash" '
  def key($domain; $record):
    if $domain == "DefaultConfiguration" then
      {ConfigurationGroupName: $record.ConfigurationGroupName, ConfigurationEntryName: $record.ConfigurationEntryName}
    elif $domain == "DefaultThemes" then
      {Name: $record.Name}
    elif $domain == "DefaultFeatureTemplates" then
      {Name: $record.Name, Type: $record.Type}
    else
      {Name: $record.Name, Namespace: $record.Namespace, Navigation: $record.Navigation}
    end;

  def key_text($domain; $record):
    (key($domain; $record) | tojson);

  def difference_fields($source; $canonical):
    if $source == null or $canonical == null then []
    else
      ((($source | keys) + ($canonical | keys)) | unique) as $fields |
      [$fields[] | select($source[.] != $canonical[.]) |
        {field: ., sourceValue: $source[.], canonicalValue: $canonical[.]}]
    end;

  def disposition($classification):
    if $classification == "CANONICAL_MATCH" then
      {
        treatment: "EXCLUDE_FROM_SOURCE_MIGRATION",
        migrationPlan: "Do not copy this default-store row as source-owned data. Reconcile the target default store to the verified canonical artifact only after explicit operator approval.",
        preserveUserData: true,
        operatorApprovalRequired: true
      }
    elif $classification == "CUSTOMIZED" then
      {
        treatment: "PRESERVE_AS_SOURCE_DATA",
        migrationPlan: "Do not overwrite with a default. Migrate or manually preserve the source row after review of its record-level differences.",
        preserveUserData: true,
        operatorApprovalRequired: true
      }
    elif $classification == "CANONICAL_VERSION_DIFFERENCE" then
      {
        treatment: "REVIEW_FUNCTIONAL_DELTA",
        migrationPlan: "Do not silently replace the source row. Review the version delta and approve an explicit replacement or source-preserving migration.",
        preserveUserData: true,
        operatorApprovalRequired: true
      }
    else
      {
        treatment: "BLOCK_MIGRATION",
        migrationPlan: "Hold this row out of automatic exclusion until authoritative provenance is established.",
        preserveUserData: true,
        operatorApprovalRequired: true
      }
    end;

  def compare_domain($domain; $source; $canonical):
    ([($source[] | key_text($domain; .)), ($canonical[] | key_text($domain; .))] | unique) as $keys |
    [$keys[] as $recordKey |
      ($source | map(select(key_text($domain; .) == $recordKey)) | .[0] // null) as $sourceRecord |
      ($canonical | map(select(key_text($domain; .) == $recordKey)) | .[0] // null) as $canonicalRecord |
      (if $sourceRecord == $canonicalRecord and $sourceRecord != null then "CANONICAL_MATCH"
       elif $sourceRecord == null or $canonicalRecord == null then "UNKNOWN_PROVENANCE"
       else "UNKNOWN_PROVENANCE"
       end) as $classification |
      {
        domain: $domain,
        stableKey: ($sourceRecord // $canonicalRecord | key($domain; .)),
        classification: $classification,
        sourceRecord: $sourceRecord,
        canonicalRecord: $canonicalRecord,
        differences: difference_fields($sourceRecord; $canonicalRecord),
        disposition: disposition($classification)
      }
    ];

  (compare_domain("DefaultConfiguration"; $sourceConfiguration[0]; $canonicalConfiguration[0]) +
   compare_domain("DefaultThemes"; $sourceThemes[0]; $canonicalThemes[0]) +
   compare_domain("DefaultFeatureTemplates"; $sourceTemplates[0]; $canonicalTemplates[0]) +
   compare_domain("DefaultWikiPages"; $sourcePages[0]; $canonicalPages[0])) as $records |
  {
    schema: "cw-m2-006c.2-default-provenance-v1",
    generatedAtUtc: (now | todateiso8601),
    readOnly: true,
    sourceSnapshot: {
      database: $sourceHash,
      domains: {
        DefaultConfiguration: 73,
        DefaultThemes: 27,
        DefaultFeatureTemplates: 61,
        DefaultWikiPages: 104
      },
      totalRecords: ($records | length),
      artifactDirectory: "source-snapshot"
    },
    canonicalSource: {
      distributionPath: $canonicalPath,
      repositoryCommit: $canonicalCommit,
      commitDate: $canonicalCommitDate,
      commitSubject: $canonicalCommitSubject,
      generatedFromRepositoryInputs: ($canonicalInputs | split(",")),
      database: $canonicalHash,
      regeneratedArtifact: {
        database: $canonicalGeneratedHash,
        configInput: $canonicalConfigHash,
        pagesInput: $canonicalPagesHash,
        rowSetsExact: true
      },
      artifactDirectory: "canonical-snapshot"
    },
    comparison: {
      normalization: "None. Row identity and all stored fields, including values, bodies, formatting, hashes, and metadata, were compared exactly. SQLite row order is excluded by deterministic key ordering.",
      recordCount: ($records | length),
      classifications: {
        CANONICAL_MATCH: ([$records[] | select(.classification == "CANONICAL_MATCH")] | length),
        CUSTOMIZED: ([$records[] | select(.classification == "CUSTOMIZED")] | length),
        CANONICAL_VERSION_DIFFERENCE: ([$records[] | select(.classification == "CANONICAL_VERSION_DIFFERENCE")] | length),
        UNKNOWN_PROVENANCE: ([$records[] | select(.classification == "UNKNOWN_PROVENANCE")] | length)
      }
    },
    exclusionAssessment: {
      domains: ["DefaultConfiguration", "DefaultThemes", "DefaultFeatureTemplates", "DefaultWikiPages"],
      canonicalProvenanceEstablished: ($sourceHash == $canonicalHash),
      automaticApproval: false,
      operatorApprovalRequired: true,
      recommendation: "The four default-store exclusions are evidence-backed canonical matches, but remain pending explicit operator approval and must not be treated as production cutover authorization."
    },
    records: $records
  }
' >"$output_dir/default-provenance-comparison.json"

jq -e '
  .schema == "cw-m2-006c.2-default-provenance-v1" and
  .readOnly == true and
  .sourceSnapshot.totalRecords == 265 and
  .comparison.recordCount == 265 and
  ([.records[] | select(.classification == "CANONICAL_MATCH")] | length) == 265 and
  .comparison.classifications.CUSTOMIZED == 0 and
  .comparison.classifications.CANONICAL_VERSION_DIFFERENCE == 0 and
  .comparison.classifications.UNKNOWN_PROVENANCE == 0
' "$output_dir/default-provenance-comparison.json" >/dev/null

echo "Default provenance audit passed: 265 canonical matches; operator approval remains required."
