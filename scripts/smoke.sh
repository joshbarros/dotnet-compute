#!/usr/bin/env bash
# Drives the composed stack over HTTP. Every code carries a run id, so a kept Postgres volume never collides.
# Needs: curl, python3. The stack must be up: docker compose up --build --wait
set -euo pipefail

API="${API:-http://127.0.0.1:5215}"
RABBIT="${RABBIT:-http://127.0.0.1:15705}"
RUN="$(date +%s)$((RANDOM % 900 + 100))"
JSON='content-type: application/json'

say() { printf '  %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
jget() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)"; }

open_pool() { # code hourCapacity -> prints nothing
  curl -sf -X POST "$API/api/pools" -H "$JSON" \
    -d "{\"code\":\"$1\",\"name\":\"Smoke Pool\",\"gpuModel\":\"H100\",\"region\":\"us-east1\",\"hourCapacity\":$2}" >/dev/null \
    || fail "could not open pool $1"
}
book() { # code reference hours -> prints reservation id
  curl -sf -X POST "$API/api/reservations" -H "$JSON" \
    -d "{\"poolCode\":\"$1\",\"reference\":\"$2\",\"hours\":$3}" | jget "['id']"
}
reservation_field() { curl -sf "$API/api/reservations/$1" | jget "['$2']"; }
remaining() { curl -sf "$API/api/pools/$1" | jget "['hoursLeft']"; }

wait_for() { # description command... : polls up to 30s until the command succeeds
  local what="$1"; shift
  for _ in $(seq 1 60); do
    if "$@" >/dev/null 2>&1; then return 0; fi
    sleep 0.5
  done
  fail "timed out waiting for $what"
}
status_is() { [ "$(reservation_field "$1" status)" = "$2" ]; }
remaining_is() { [ "$(remaining "$1")" = "$2" ]; }
terminal() { case "$(reservation_field "$1" status)" in Leased|Rejected|Cancelled) return 0 ;; *) return 1 ;; esac; }
event_types() { curl -sf "$API/api/reservations/$1/events" | python3 -c "import sys,json; print(' '.join(e['type'] for e in json.load(sys.stdin)))"; }
dlq_is() { [ "$(dlq_count)" = "$1" ]; }
dlq_count() { curl -sf -u platform:platform "$RABBIT/api/queues/%2F/leases.work.dlq" | jget "['messages']"; }

curl -sf "$API/api/health" >/dev/null || fail "API is not healthy at $API; run: docker compose up --build --wait"

echo "1. input is validated at the door"
open_pool "V-$RUN" 10
for body in \
  "{\"poolCode\":\"V-$RUN\",\"reference\":\"\",\"hours\":1}" \
  "{\"poolCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"hours\":0}" \
  "{\"poolCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"hours\":25}" \
  "{\"poolCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"hours\":4294967297}" \
  "{\"poolCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"hours\":9223372036854775807}"; do
  code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/reservations" -H "$JSON" -d "$body")"
  [ "$code" = 400 ] || fail "expected 400 for $body, got $code"
done
say "empty, zero, over-limit and overflowing hour counts are all 400"

echo "2. happy path"
open_pool "H-$RUN" 20
ID="$(book "H-$RUN" "TEAM-$RUN-1" 10)"
wait_for "reservation to be Leased" status_is "$ID" Leased
[ "$(reservation_field "$ID" feeCents)" = 3200 ] || fail "fee should be 10 x 320 cents (above the 1500 minimum)"
[ "$(curl -sf "$API/api/leases/$ID" | jget "['status']")" = Issued ] || fail "lease not issued"
wait_for "remaining hours to be 10" remaining_is "H-$RUN" 10
TYPES="$(event_types "$ID")"
for t in hours.reserve hours.held lease.issued; do
  case "$TYPES" in *"$t"*) ;; *) fail "Kafka log is missing $t (has: $TYPES)" ;; esac
done
say "leased, fee 3200, remaining 10, Kafka log has the three steps"

echo "3. last hour: three reservations, one commits, two are short and write no hold"
open_pool "L-$RUN" 1
TMP="$(mktemp -d)"
for n in A B C; do ( book "L-$RUN" "TEAM-$RUN-$n" 1 >"$TMP/$n" ) & done
wait
IDS=""
for n in A B C; do [ -s "$TMP/$n" ] || fail "reservation $n was not accepted"; IDS="$IDS $(cat "$TMP/$n")"; done
rm -rf "$TMP"
for i in $IDS; do wait_for "reservation $i to settle" terminal "$i"; done
LEASED=0; SHORT=0
for i in $IDS; do
  s="$(reservation_field "$i" status)"
  if [ "$s" = Leased ]; then LEASED=$((LEASED + 1)); fi
  if [ "$s" = Rejected ]; then
    case "$(reservation_field "$i" reason)" in Short*) SHORT=$((SHORT + 1)); case "$(event_types "$i")" in *hours.held*) fail "the short loser has a hold" ;; esac ;; esac
  fi
done
[ "$LEASED" = 1 ] && [ "$SHORT" = 2 ] || fail "expected 1 leased and 2 short, got $LEASED leased and $SHORT short"
[ "$(remaining "L-$RUN")" = 0 ] || fail "remaining should be 0"
say "1 leased, 2 short, remaining 0, no hold for the losers"

echo "4. poison: committed as rejected, dead-lettered, the hours come back"
open_pool "P-$RUN" 5
BEFORE="$(dlq_count)"
IDP="$(book "P-$RUN" "TEAM-POISON-$RUN" 2)"
wait_for "poison reservation to be Rejected" status_is "$IDP" Rejected
wait_for "hours to be restored to 5" remaining_is "P-$RUN" 5
wait_for "leases.work.dlq to gain one message" dlq_is $((BEFORE + 1))
[ "$(curl -sf "$API/api/leases/$IDP" | jget "['status']")" = Rejected ] || fail "rejection should be committed on the lease row"
[ "$(reservation_field "$IDP" hoursReleasedAt)" != None ] || fail "reservation should record the release"
say "rejected, dead-lettered (+1 in leases.work.dlq), remaining back to 5"

echo "5. cancellation races the final mark: the cancellation wins, the issued lease stays"
open_pool "R-$RUN" 6
WON=0
for attempt in 1 2 3; do
  IDR="$(book "R-$RUN" "TEAM-$RUN-R$attempt" 2)"
  # The lease exists before the reservation is marked Leased (the final mark is delayed in compose).
  wait_for "lease to exist" curl -sf "$API/api/leases/$IDR"
  if status_is "$IDR" Held; then WON=1; break; fi
  say "window missed on attempt $attempt (reservation already Leased); the cancellation still has to hold; retrying"
  curl -sf -X POST "$API/api/reservations/$IDR/cancellation" >/dev/null || true
  wait_for "reservation to be Cancelled" status_is "$IDR" Cancelled
done
[ "$WON" = 1 ] || fail "never caught the reservation between lease issued and final mark"
curl -sf -X POST "$API/api/reservations/$IDR/cancellation" >/dev/null || fail "cancellation was refused"
wait_for "reservation to be Cancelled" status_is "$IDR" Cancelled
sleep 3 # longer than the final-mark delay: the late mark must not undo the cancellation
status_is "$IDR" Cancelled || fail "the final mark overwrote the cancellation"
[ "$(curl -sf "$API/api/leases/$IDR" | jget "['status']")" = Issued ] || fail "the issued lease was rolled back"
[ "$(reservation_field "$IDR" leaseNumber)" = None ] || fail "the late final mark should have been skipped"
wait_for "hours to come back" remaining_is "R-$RUN" 6
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/reservations/$IDR/cancellation")"
[ "$code" = 409 ] || fail "second cancellation should be 409, got $code"
say "reservation Cancelled, lease still Issued, remaining 6, second cancellation 409"

echo "compute smoke passed"
