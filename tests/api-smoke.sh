#!/usr/bin/env bash
set -euo pipefail

BASE=http://127.0.0.1:5099
LOG=$(mktemp)
ASPNETCORE_ENVIRONMENT=Development Database__AutoMigrate=false \
  dotnet run --project src/BankCore.Api -c Release --no-build --urls "$BASE" >"$LOG" 2>&1 &
PID=$!
cleanup() {
  exit_code=$?
  kill "$PID" 2>/dev/null || true
  if [ "$exit_code" -ne 0 ]; then
    echo 'API log on failure:'
    cat "$LOG"
  fi
  rm -f "$LOG"
}
trap cleanup EXIT

healthy=false
for i in {1..40}; do
  if curl --silent --fail "$BASE/health" > /dev/null; then healthy=true; break; fi
  sleep 1
done
if [ "$healthy" != true ]; then echo 'API did not start'; exit 1; fi

# Disposable test credentials, never real banking or production credentials.
A="smoke_alice_$(date +%s)"
B="smoke_bob_$(date +%s)"
PASSWORD='CI_TEST_PASSWORD_123456789'
register() {
  curl --silent --show-error --fail-with-body -X POST "$BASE/api/auth/register" \
    -H 'Content-Type: application/json' -d "{\"username\":\"$1\",\"password\":\"$PASSWORD\"}" > /dev/null
}
login() {
  curl --silent --show-error --fail-with-body -X POST "$BASE/api/auth/login" \
    -H 'Content-Type: application/json' -d "{\"username\":\"$1\",\"password\":\"$PASSWORD\"}" | jq -r '.accessToken'
}
register "$A"
register "$B"
TOKEN_A=$(login "$A")
TOKEN_B=$(login "$B")
if [ -z "$TOKEN_A" ] || [ "$TOKEN_A" = null ]; then echo 'JWT login failed'; exit 1; fi
ACCOUNT_A=$(curl --silent --show-error --fail-with-body -X POST "$BASE/api/accounts" \
  -H "Authorization: Bearer $TOKEN_A" -H 'Content-Type: application/json' \
  -d '{"type":"Checking"}' | jq -r '.id')
ACCOUNT_B=$(curl --silent --show-error --fail-with-body -X POST "$BASE/api/accounts" \
  -H "Authorization: Bearer $TOKEN_B" -H 'Content-Type: application/json' \
  -d '{"type":"Checking"}' | jq -r '.id')

status() { curl --silent -o /dev/null -w '%{http_code}' "$@"; }
[ "$(status "$BASE/api/accounts/$ACCOUNT_A")" = '401' ] || { echo 'Anonymous read not blocked'; exit 1; }
[ "$(status "$BASE/api/accounts/$ACCOUNT_A" -H "Authorization: Bearer $TOKEN_B")" = '404' ] || { echo 'Account privacy failed'; exit 1; }
[ "$(status -X POST "$BASE/api/accounts/$ACCOUNT_A/withdrawals" \
  -H "Authorization: Bearer $TOKEN_B" -H 'Idempotency-Key: smoke-forbidden-0001' \
  -H 'Content-Type: application/json' -d '{"amount":1.00,"currency":"USD"}')" = '403' ] || { echo 'Account ownership enforcement failed'; exit 1; }

curl --silent --show-error --fail-with-body -X POST "$BASE/api/accounts/$ACCOUNT_A/deposits" \
  -H "Authorization: Bearer $TOKEN_A" -H 'Idempotency-Key: smoke-funding-0001' \
  -H 'Content-Type: application/json' -d '{"amount":100.00,"currency":"USD"}' > /dev/null

PAYLOAD="{\"fromAccountId\":\"$ACCOUNT_A\",\"toAccountId\":\"$ACCOUNT_B\",\"amount\":35.25,\"currency\":\"USD\"}"
transfer() {
  curl --silent --show-error --fail-with-body -X POST "$BASE/api/transfers" \
    -H "Authorization: Bearer $TOKEN_A" -H 'Idempotency-Key: smoke-transfer-0001' \
    -H 'Content-Type: application/json' -d "$PAYLOAD"
}
FIRST=$(transfer)
SECOND=$(transfer)
[ "$(jq -r '.operationId' <<<"$FIRST")" = "$(jq -r '.operationId' <<<"$SECOND")" ] || { echo 'Idempotent replay changed ID'; exit 1; }
[ "$(jq -r '.replayed' <<<"$SECOND")" = true ] || { echo 'Expected replayed:true'; exit 1; }
[ "$(status -X POST "$BASE/api/transfers" \
  -H "Authorization: Bearer $TOKEN_A" -H 'Idempotency-Key: smoke-transfer-0001' \
  -H 'Content-Type: application/json' \
  -d "{\"fromAccountId\":\"$ACCOUNT_A\",\"toAccountId\":\"$ACCOUNT_B\",\"amount\":36.25,\"currency\":\"USD\"}")" = '409' ] || { echo 'Conflicting idempotency payload accepted'; exit 1; }

BALANCE_A=$(curl --silent --show-error --fail-with-body "$BASE/api/accounts/$ACCOUNT_A" \
  -H "Authorization: Bearer $TOKEN_A" | jq -r '.balance')
BALANCE_B=$(curl --silent --show-error --fail-with-body "$BASE/api/accounts/$ACCOUNT_B" \
  -H "Authorization: Bearer $TOKEN_B" | jq -r '.balance')
[ "$BALANCE_A" = '64.75' ] && [ "$BALANCE_B" = '35.25' ] || { echo "Bad balances: $BALANCE_A / $BALANCE_B"; exit 1; }
AUDIT=$(curl --silent --show-error --fail-with-body "$BASE/api/accounts/$ACCOUNT_A/audit" -H "Authorization: Bearer $TOKEN_A")
[ "$(jq -r '.balanceMatchesLedger' <<<"$AUDIT")" = true ] || { echo 'Ledger reconciliation failed'; exit 1; }
[ "$(jq -r '.invalidTransferCount' <<<"$AUDIT")" = '0' ] || { echo 'Transfer legs did not balance'; exit 1; }

curl --silent --show-error --fail "$BASE/openapi/v1.json" > /dev/null
printf 'HTTP smoke passed: JWT, access control, idempotency, balances, ledger, OpenAPI.\n'