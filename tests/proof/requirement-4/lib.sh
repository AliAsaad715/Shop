# Helpers for the Requirement 4 proof scripts (task R4-10). Each script sources this file; it isn't run on its own.
# They talk to the running Compose stack: the API, RabbitMQ's management API, Mailpit's API and Postgres.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."

readonly EMAIL_QUEUE=email.order-confirmation
readonly DEAD_LETTERS=email.order-confirmation.dlq
readonly PRODUCT=019a2180-0000-7000-8000-000000000202 # Cotton T-Shirt; the warehouse seed stocks 100
readonly PASSWORD='Proof-Pass-123!'

step() { printf '\n== %s\n' "$*"; }
say() { printf '   %s\n' "$*"; }
plural() { if [[ $1 == 1 ]]; then echo "$1 $2"; else echo "$1 ${2}s"; fi; } # plural 3 email: "3 emails"
fail() {
  printf 'FAILED: %s\n' "$*" >&2
  exit 1
}

# Shows a command, runs it quietly, and shows its output only if it fails
run() {
  say "\$ $*"
  local output
  output=$("$@" 2>&1) || {
    printf '%s\n' "$output" >&2
    fail "$*"
  }
}

# A Compose setting as Compose resolves it: from the environment, else from .env, else its default
setting() {
  local name=$1 fallback=$2 value
  if [[ -n ${!name:-} ]]; then
    printf '%s' "${!name}"
    return
  fi
  value=$(sed -n "s/^$name=//p" .env 2>/dev/null | tail -n 1)
  value=${value#[\"\']}
  value=${value%[\"\']}
  printf '%s' "${value:-$fallback}"
}

for service in api rabbitmq mailpit postgres; do
  docker compose ps --status running --services | grep -qx "$service" ||
    fail "The $service service isn't running. Start the stack first: docker compose up -d --wait"
done

# Where Compose published each service, so another port or project name works too
published() { echo "http://localhost:$(docker compose port "$1" "$2" | sed 's/.*://')"; }
API=${API_URL:-$(published api 8080)}
RABBIT=${RABBITMQ_UI_URL:-$(published rabbitmq 15672)}
MAILPIT=${MAILPIT_URL:-$(published mailpit 8025)}
RABBIT_AUTH="$(setting RABBITMQ_USER shop):$(setting RABBITMQ_PASSWORD shop)"

# A broker set up before R4-09 still has the email queue with x-delivery-limit 5, which the hosts can't declare
limit=$(curl -fsS -u "$RABBIT_AUTH" "$RABBIT/api/queues/%2F/$EMAIL_QUEUE" 2>/dev/null |
  jq -r '.arguments."x-delivery-limit" // "unset"') || fail "can't read $EMAIL_QUEUE from RabbitMQ's management API at $RABBIT"
[[ $limit == 4 ]] || fail "$EMAIL_QUEUE has x-delivery-limit $limit, and this build expects 4. Follow \"Upgrading the" \
  "email queue to five deliveries (R4-09)\" in the README first"

# Checks a condition every half second until it holds, and fails after the given seconds
wait_for() {
  local what=$1 seconds=$2
  shift 2
  local deadline=$((SECONDS + seconds))
  until "$@"; do
    ((SECONDS < deadline)) || fail "waited ${seconds} s for $what"
    sleep 0.5
  done
}

# The API

sign_in() {
  local response
  response=$(curl -sS -w '\n%{http_code}' -H 'Content-Type: application/json' \
    -d "$(jq -n --arg email "$1" --arg password "$2" '{email: $email, password: $password}')" \
    "$API/api/v1/auth/login")
  case ${response##*$'\n'} in
    200) jq -r .accessToken <<<"${response%$'\n'*}" ;;
    429) fail "too many sign-ins in the last minute; wait a minute and run it again" ;;
    *) fail "signing in as $1 failed: ${response%$'\n'*}" ;;
  esac
}

# Registers a customer with a new email address. Registering isn't rate-limited; signing in is
register() {
  curl -fsS -o /dev/null -H 'Content-Type: application/json' \
    -d "$(jq -n --arg email "$1" --arg password "$PASSWORD" '{email: $email, password: $password}')" \
    "$API/api/v1/auth/register" || fail "registering $1 failed"
}

# Registers a customer and prints their token
new_customer() {
  register "$1"
  sign_in "$1" "$PASSWORD"
}

# Places one order as the token's customer and prints "<HTTP status> <seconds> <order ID>"
place_order() {
  local response status seconds
  response=$(curl -sS -w '\n%{http_code} %{time_total}' -H "Authorization: Bearer $1" \
    -H 'Content-Type: application/json' -d "{\"lines\":[{\"productId\":\"$PRODUCT\",\"quantity\":1}]}" \
    "$API/api/v1/orders")
  read -r status seconds <<<"${response##*$'\n'}"
  LC_NUMERIC=C printf '%s %.2f %s\n' "$status" "$seconds" \
    "$(jq -r '.id // "-"' <<<"${response%$'\n'*}" 2>/dev/null || echo -)"
}

# RabbitMQ, through the management API (the numbers its UI shows; they can lag a few seconds)

queue() { curl -fsS -u "$RABBIT_AUTH" "$RABBIT/api/queues/%2F/$1"; }
count() { queue "$1" | jq -r ".$2 // 0"; }
queue_is() { [[ $(count "$1" "$2") == "$3" ]]; }
queue_at_most() { (($(count "$1" "$2") <= $3)); }

# The first messages in a queue (100 at most), left in place: a nack with requeue, which RabbitMQ doesn't count as a
# failure
peek() {
  curl -fsS -u "$RABBIT_AUTH" -H 'Content-Type: application/json' \
    -d '{"count": 100, "ackmode": "ack_requeue_true", "encoding": "auto"}' "$RABBIT/api/queues/%2F/$1/get"
}

# Mailpit: how many emails match a search, such as an order ID or to:someone@shop.test

emails() { curl -fsS -G --data-urlencode "query=$1" "$MAILPIT/api/v1/search" | jq -r '.messages_count'; }
emails_are() { [[ $(emails "$1") == "$2" ]]; }
subjects() { curl -fsS -G --data-urlencode "query=$1" "$MAILPIT/api/v1/search" | jq -r '.messages[].Subject'; }

# Postgres

sql() { docker compose exec -T postgres sh -c 'psql -X -q -At -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v order="$1"' sh "$1"; }

# The order.placed outbox row of an order: its ID is also the RabbitMQ message ID
message_id() {
  sql "$1" <<'SQL'
SELECT id FROM orders.outbox_messages WHERE type = 'order.placed' AND payload->>'orderId' = :'order'
SQL
}

# How many times the email worker recorded an order's confirmation as sent
processed() {
  sql "$1" <<'SQL'
SELECT count(*) FROM notifications.processed_messages p
JOIN orders.outbox_messages o ON o.id = p.message_id
WHERE o.type = 'order.placed' AND o.payload->>'orderId' = :'order'
SQL
}

# The worker's log lines since a moment, without Compose's prefix
worker_log() { docker compose logs --no-log-prefix --since "$1" worker 2>&1; }
