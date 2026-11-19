#!/usr/bin/env bash
# Interview step 4 (task R4-10). The worker signs in to the mail server with a wrong password. RabbitMQ delivers the
# confirmation five times (the first try plus four retries), then parks it in the DLQ. After the password is fixed,
# the message is moved back and the email arrives once.
#
#   tests/proof/requirement-4/3-mail-failure.sh
#
# Mailpit stands in for a real provider: compose.mail-failure.yaml makes it ask for a password, so no real mail
# account is needed. At the end, even if a step fails, Mailpit and the worker go back to the settings in .env.
# Restarting Mailpit empties its inbox. The move back takes everything in the DLQ, not just this run's message.
source "$(dirname "$0")/lib.sh"
readonly OVERRIDE=tests/proof/requirement-4/compose.mail-failure.yaml
trap 'docker compose up -d --no-build --wait mailpit worker >/dev/null 2>&1 || true' EXIT

# Recreates services with Mailpit asking for a password, and the worker signing in with the given one
with_password() {
  local password=$1
  shift
  say "\$ PROOF_SMTP_PASSWORD=$password docker compose -f compose.yaml -f $OVERRIDE up -d --wait $*"
  PROOF_SMTP_PASSWORD=$password docker compose -f compose.yaml -f "$OVERRIDE" up -d --no-build --wait "$@" >/dev/null 2>&1 ||
    fail "recreating $* failed"
}

docker compose start worker >/dev/null 2>&1
customer="mailfail-$(date +%s)@shop.test"
token=$(new_customer "$customer")
wait_for "the email queue to be empty before starting" 60 queue_is "$EMAIL_QUEUE" messages 0

step "4. Mail failure: a wrong password, five failed deliveries, then the DLQ"
with_password wrong-password mailpit worker
since=$(date -u +%Y-%m-%dT%H:%M:%SZ)
dead_before=$(count "$DEAD_LETTERS" messages)
read -r status seconds order < <(place_order "$token")
[[ $status == 201 ]] || fail "the order returned $status"
say "order: $status in $seconds s ($order)"
wait_for "the message to reach $DEAD_LETTERS" 90 queue_is "$DEAD_LETTERS" messages $((dead_before + 1))
say "worker log, one line per delivery:"
worker_log "$since" | grep "The email settings are wrong.*$order" |
  sed -E 's/.*\(([^)]*)\); check.*\(([0-9]+) failed deliveries before this one\).*/\1, after \2 failed deliveries/' |
  while read -r line; do say "  $line"; done
id=$(message_id "$order")
dead=$(peek "$DEAD_LETTERS" | jq --arg id "$id" '.[] | select(.properties.message_id == $id)')
say "RabbitMQ: $DEAD_LETTERS has $(plural "$(count "$DEAD_LETTERS" messages)" message); this one is $id," \
  "x-first-death-reason $(jq -r '.properties.headers."x-first-death-reason"' <<<"$dead")"
say "Mailpit: $(plural "$(emails "$order")" email) for order $order"

step "4b. Fix the password, move the message back, and the email arrives once"
with_password right-password worker
move_back='{"src-protocol": "amqp091", "src-uri": "amqp://", "src-queue": "email.order-confirmation.dlq", "dest-protocol": "amqp091", "dest-uri": "amqp://", "dest-queue": "email.order-confirmation", "src-delete-after": "queue-length"}'
run docker compose exec -T rabbitmq rabbitmqctl set_parameter shovel email-dlq-move-back "$move_back"
wait_for "the email for $order" 60 emails_are "$order" 1
wait_for "$EMAIL_QUEUE to empty" 30 queue_is "$EMAIL_QUEUE" messages 0
wait_for "the DLQ to give its messages back" 30 queue_at_most "$DEAD_LETTERS" messages "$dead_before"
say "Mailpit: $(plural "$(emails "$order")" email) for order $order"
say "notifications.processed_messages: $(plural "$(processed "$order")" row) for it"
say "RabbitMQ: $DEAD_LETTERS has $(plural "$(count "$DEAD_LETTERS" messages)" message); shovels left:" \
  "$(docker compose exec -T rabbitmq rabbitmqctl list_parameters -q | grep -c shovel || true)"
