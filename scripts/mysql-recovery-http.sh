#!/usr/bin/env bash
set -Eeuo pipefail
: "${RECOVERY_BASE_URL:?RECOVERY_BASE_URL is required}"
: "${RECOVERY_USERNAME:?RECOVERY_USERNAME is required}"
: "${RECOVERY_PASSWORD:?RECOVERY_PASSWORD is required}"
: "${RECOVERY_PAGE_NAVIGATION:?RECOVERY_PAGE_NAVIGATION is required}"
: "${RECOVERY_PAGE_ID:?RECOVERY_PAGE_ID is required}"
: "${RECOVERY_HISTORICAL_REVISION:?RECOVERY_HISTORICAL_REVISION is required}"
: "${RECOVERY_FILE_NAVIGATION:?RECOVERY_FILE_NAVIGATION is required}"
: "${RECOVERY_FILE_SHA256:?RECOVERY_FILE_SHA256 is required}"
: "${RECOVERY_CURRENT_FILE_SHA256:?RECOVERY_CURRENT_FILE_SHA256 is required}"
: "${RECOVERY_HISTORICAL_FILE_SHA256:?RECOVERY_HISTORICAL_FILE_SHA256 is required}"
: "${RECOVERY_RESULTS_JSON:=/tmp/continuumwiki-recovery-http-results.json}"
command -v curl >/dev/null || { echo 'curl is required' >&2; exit 2; }
command -v sha256sum >/dev/null || { echo 'sha256sum is required' >&2; exit 2; }
cookie=$(mktemp); work=$(mktemp -d)
trap 'rm -f "$cookie"; rm -rf "$work"' EXIT
checks=(); failures=0
content_failures=0
workflow_failures=0
smoke_failures=0
check() {
  local id="$1" route="$2" expected="$3" body="$work/$1.body" status
  status=$(curl -sS -L -b "$cookie" -c "$cookie" -o "$body" -w '%{http_code}' "$RECOVERY_BASE_URL$route" || true)
  if [[ "$status" == "$expected" ]]; then
    checks+=("{\"id\":\"$id\",\"route\":\"$route\",\"expected\":\"$expected\",\"observed\":\"$status\",\"status\":\"PASS\"}")
  else
    checks+=("{\"id\":\"$id\",\"route\":\"$route\",\"expected\":\"$expected\",\"observed\":\"$status\",\"status\":\"FAIL\"}"); failures=$((failures+1))
  fi
}
assert_content() {
  local id="$1" body="$2" pattern="$3"
  if ! grep -Eiq "$pattern" "$body"; then
    echo "$id response-content assertion failed: $pattern" >&2
    failures=$((failures+1)); content_failures=$((content_failures+1))
  fi
}
assert_workflow() {
  local id="$1" actual="$2" expected="$3"
  if [[ "$actual" != "$expected" ]]; then
    echo "$id workflow assertion failed: expected $expected, observed $actual" >&2
    failures=$((failures+1)); workflow_failures=$((workflow_failures+1))
  fi
}
smoke_get() {
  local id="$1" route="$2" pattern="$3" body="$work/$1.smoke.body" status
  status=$(curl -sS -L -b "$cookie" -c "$cookie" -o "$body" -w '%{http_code}' "$RECOVERY_BASE_URL$route" || true)
  if [[ "$status" != 200 ]] || ! grep -Eiq "$pattern" "$body"; then
    echo "$id smoke assertion failed: HTTP $status / $pattern" >&2
    failures=$((failures+1)); smoke_failures=$((smoke_failures+1))
  fi
}
login="$work/login.html"
curl -fsS -c "$cookie" "$RECOVERY_BASE_URL/Identity/Account/Login" -o "$login"
token=$(grep -o 'name="__RequestVerificationToken"[^>]*' "$login" | sed -n 's/.*value="\([^"]*\)".*/\1/p')
login_status=$(curl -sS -o "$work/login-result" -w '%{http_code}' -b "$cookie" -c "$cookie" -X POST "$RECOVERY_BASE_URL/Identity/Account/Login" --data-urlencode "Input.Username=$RECOVERY_USERNAME" --data-urlencode "Input.Password=$RECOVERY_PASSWORD" --data-urlencode 'Input.RememberMe=false' --data-urlencode "__RequestVerificationToken=$token")
[[ "$login_status" == 302 ]] || { echo "REC-01 login failed: HTTP $login_status" >&2; exit 1; }
checks+=("{\"id\":\"REC-01\",\"route\":\"/Identity/Account/Login\",\"expected\":\"302\",\"observed\":\"$login_status\",\"status\":\"PASS\"}")
smoke_get HTTP-HOMEPAGE / 'Continuum|TightWiki|wiki'

workflow_page='cw_m3_http_page'
create_status=$(curl -sS -D "$work/create-page.headers" -o "$work/create-page.body" -w '%{http_code}' -b "$cookie" -c "$cookie" -X POST "$RECOVERY_BASE_URL/$workflow_page/Create" \
  --data-urlencode 'Id=0' --data-urlencode 'Revision=0' \
  --data-urlencode 'Name=CW M3 HTTP Page' --data-urlencode 'Description=HTTP workflow fixture' \
  --data-urlencode 'ChangeSummary=Create HTTP workflow fixture' \
  --data-urlencode 'Body=Created through the authenticated HTTP workflow.')
assert_workflow HTTP-CREATE "$create_status" 302
create_location=$(sed -n 's/^Location: //p' "$work/create-page.headers" | tr -d '\r' | tail -1)
if [[ -n "$create_location" ]]; then
  curl -sS -L -b "$cookie" -c "$cookie" "$RECOVERY_BASE_URL$create_location" -o "$work/create-notification.body" || true
fi
created_page=$(curl -sS -L -b "$cookie" -c "$cookie" -o "$work/created-page.body" -w '%{http_code}' "$RECOVERY_BASE_URL/$workflow_page")
assert_workflow HTTP-CREATE-READ "$created_page" 200
assert_content HTTP-CREATE-READ "$work/created-page.body" 'Created through the authenticated HTTP workflow'

edit_form=$(curl -sS -L -b "$cookie" -c "$cookie" "$RECOVERY_BASE_URL/$workflow_page/Edit")
edit_id=$(printf '%s' "$edit_form" | sed -n 's/.*name="Id"[^>]*value="\([0-9]*\)".*/\1/p' | head -1)
edit_revision=$(printf '%s' "$edit_form" | sed -n 's/.*name="Revision"[^>]*value="\([0-9]*\)".*/\1/p' | head -1)
if [[ -z "$edit_id" || -z "$edit_revision" ]]; then
  echo 'HTTP-EDIT could not parse page identity from edit form' >&2
  failures=$((failures+1)); workflow_failures=$((workflow_failures+1))
else
  edit_status=$(curl -sS -o "$work/edit-page.body" -w '%{http_code}' -b "$cookie" -c "$cookie" -X POST "$RECOVERY_BASE_URL/$workflow_page/Edit" \
    --data-urlencode "Id=$edit_id" --data-urlencode "Revision=$edit_revision" \
    --data-urlencode 'Name=CW M3 HTTP Page' --data-urlencode 'Description=HTTP workflow fixture edited' \
    --data-urlencode 'ChangeSummary=Edit HTTP workflow fixture' \
    --data-urlencode 'Body=Edited through the authenticated HTTP workflow.')
  assert_workflow HTTP-EDIT "$edit_status" 200
  edited_page=$(curl -sS -L -b "$cookie" -c "$cookie" -o "$work/edited-page-read.body" -w '%{http_code}' "$RECOVERY_BASE_URL/$workflow_page")
  assert_workflow HTTP-EDIT-READ "$edited_page" 200
  assert_content HTTP-EDIT-READ "$work/edited-page-read.body" 'Edited through the authenticated HTTP workflow'
  revision_page=$(curl -sS -L -b "$cookie" -c "$cookie" -o "$work/edited-page-revisions.body" -w '%{http_code}' "$RECOVERY_BASE_URL/$workflow_page/Revisions")
  assert_workflow HTTP-EDIT-REVISION "$revision_page" 200
  assert_content HTTP-EDIT-REVISION "$work/edited-page-revisions.body" 'Edit HTTP workflow fixture'
fi

check REC-02 /Admin/Database 200
assert_content REC-02 "$work/REC-02.body" 'Database|MariaDB|MySQL'
check REC-03 "/$RECOVERY_PAGE_NAVIGATION" 200
assert_content REC-03 "$work/REC-03.body" 'Recovery Article|Current Recovery'
check REC-04 "/$RECOVERY_PAGE_NAVIGATION/Revisions" 200
assert_content REC-04 "$work/REC-04.body" 'Revision|Recovery Article'
check REC-05 "/Page/$RECOVERY_PAGE_ID/Revision" 200
check REC-06 "/$RECOVERY_PAGE_NAVIGATION/Compare/$RECOVERY_HISTORICAL_REVISION" 200
assert_content REC-06 "$work/REC-06.body" 'Historical Recovery|Current Recovery|diff'
check REC-07 '/Page/Search?search=Recovery' 200
assert_content REC-07 "$work/REC-07.body" 'Recovery|Search'
smoke_get HTTP-TAGS "/Tags/Browse/RecoveryTag" 'RecoveryTag|Tag'
check REC-08 "/$RECOVERY_PAGE_NAVIGATION/Comments" 200
assert_content REC-08 "$work/REC-08.body" 'Recovered comment|Comments'
check REC-09 "/File/Binary/$RECOVERY_PAGE_NAVIGATION/$RECOVERY_FILE_NAVIGATION" 200
current_hash=$(sha256sum "$work/REC-09.body" | cut -d' ' -f1)
if [[ "$current_hash" != "$RECOVERY_CURRENT_FILE_SHA256" ]]; then echo "REC-10 current attachment checksum mismatch: $current_hash" >&2; failures=$((failures+1)); fi
checks+=("{\"id\":\"REC-10\",\"route\":\"/File/Binary/$RECOVERY_PAGE_NAVIGATION/$RECOVERY_FILE_NAVIGATION\",\"expected\":\"sha256:$RECOVERY_CURRENT_FILE_SHA256\",\"observed\":\"sha256:$current_hash\",\"status\":\"$([[ $current_hash == $RECOVERY_CURRENT_FILE_SHA256 ]] && echo PASS || echo FAIL)\"}")
check REC-11 "/File/Binary/$RECOVERY_PAGE_NAVIGATION/$RECOVERY_FILE_NAVIGATION/0" 200
historical_hash=$(sha256sum "$work/REC-11.body" | cut -d' ' -f1)
if [[ "$historical_hash" != "$RECOVERY_HISTORICAL_FILE_SHA256" ]]; then echo "REC-11 historical attachment checksum mismatch: $historical_hash" >&2; failures=$((failures+1)); fi
check REC-12 "/File/Revisions/$RECOVERY_PAGE_NAVIGATION/$RECOVERY_FILE_NAVIGATION" 200
check REC-13 "/$RECOVERY_PAGE_NAVIGATION/Comments" 200
check REC-14 /Page/Localization 200
assert_content REC-14 "$work/REC-14.body" 'Localization|French|français|Français'
smoke_get HTTP-FRENCH '/Page/Localization?culture=fr' 'Français|French|Langue|Localization'
check REC-15 /Profile/My 200
assert_content REC-15 "$work/REC-15.body" "$RECOVERY_USERNAME|recovery-admin"
smoke_get HTTP-THEME '/Profile/My' 'Theme|Darkly|theme'
check REC-16 /Admin/MenuItems 200
assert_content REC-16 "$work/REC-16.body" 'Menu|Migration Recovery Menu'
backup_status=$(curl -sS -o "$work/REC-17.body" -w '%{http_code}' -b "$cookie" -c "$cookie" -X POST "$RECOVERY_BASE_URL/Admin/DatabaseBackups/Create" --data-urlencode 'UserSelection=true' --data-urlencode 'YesRedirectURL=/Admin/Database' --data-urlencode 'NoRedirectURL=/Admin/Database')
backup_page="$work/REC-17-followup.body"
curl -sS -L -b "$cookie" -c "$cookie" -o "$backup_page" "$RECOVERY_BASE_URL/Admin/Database" >/dev/null || true
if [[ "$backup_status" == 302 ]] && ! grep -Eiq 'database backup.*created' "$backup_page"; then
  checks+=("{\"id\":\"REC-17\",\"route\":\"/Admin/DatabaseBackups/Create\",\"expected\":\"302 without backup-success response\",\"observed\":\"$backup_status\",\"status\":\"PASS\"}")
else
  checks+=("{\"id\":\"REC-17\",\"route\":\"/Admin/DatabaseBackups/Create\",\"expected\":\"302 without backup-success response\",\"observed\":\"$backup_status\",\"status\":\"FAIL\"}"); failures=$((failures+1))
fi
logout=$(curl -sS -o "$work/logout" -w '%{http_code}' -b "$cookie" -c "$cookie" "$RECOVERY_BASE_URL/Identity/Account/Logout")
[[ "$logout" == 200 ]] || failures=$((failures+1))
mkdir -p "$(dirname "$RECOVERY_RESULTS_JSON")"
printf '{"checks":[%s],"logout":"%s","contentAssertions":"%s","workflowAssertions":"%s","smokeAssertions":"%s"}\n' "$(IFS=,; echo "${checks[*]}")" "$logout" "$([[ $content_failures == 0 ]] && echo PASS || echo FAIL)" "$([[ $workflow_failures == 0 ]] && echo PASS || echo FAIL)" "$([[ $smoke_failures == 0 ]] && echo PASS || echo FAIL)" > "$RECOVERY_RESULTS_JSON"
[[ "$failures" -eq 0 && "${#checks[@]}" -eq 17 ]] || { echo "HTTP recovery incomplete: $RECOVERY_RESULTS_JSON" >&2; exit 1; }
echo "HTTP recovery passed: $RECOVERY_RESULTS_JSON"
