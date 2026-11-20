#!/usr/bin/env bash
# Interview step 3 (task R4-10). The worker is killed in the middle of sending an email: RabbitMQ gets the
# unacknowledged message back, delivers it again, and the email arrives once. Then the same message is delivered
# a second time on purpose, and the worker's inbox (notifications.processed_messages) stops a second email.
#
#   tests/proof/requirement-4/2-crash-mid-send.sh
#
# Pausing Mailpit makes the send hang: the worker connects and waits for the mail server to answer (up to
# Email:Timeout, 15 s a try). It unpauses Mailpit and starts the worker again at the end, even if a step fails.
source "$(dirname "$0")/lib.sh"
trap 'docker compose unpause mailpit >/dev/null 2>&1 || true; docker compose start worker >/dev/null 2>&1 || true' EXIT

docker compose start worker >/dev/null 2>&1
customer="crash-$(date +%s)@shop.test"
token=$(new_customer "$customer")
wait_for "the email queue to be empty before starting" 60 queue_is "$EMAIL_QUEUE" messages 0

step "3. Crash mid-send: the message comes back, and the email arrives once"
run docker compose pause mailpit
read -r status seconds order < <(place_order "$token")
[[ $status == 201 ]] || fail "the order returned $status"
say "order: $status in $seconds s ($order)"
wait_for "the worker to take the message" 30 queue_is "$EMAIL_QUEUE" messages_unacknowledged 1
say "RabbitMQ: 1 unacknowledged; the worker has it and is waiting for the mail server"
run docker compose kill worker
wait_for "RabbitMQ to take the message back" 30 queue_is "$EMAIL_QUEUE" messages_ready 1
failed=$(peek "$EMAIL_QUEUE" | jq -r --arg id "$(message_id "$order")" \
  '.[] | select(.properties.message_id == $id) | .properties.headers."x-delivery-count" // 0')
say "RabbitMQ: 1 ready again, x-delivery-count $failed: the lost connection returned it and counted a failed delivery"
run docker compose unpause mailpit
run docker compose start worker
wait_for "$EMAIL_QUEUE to empty" 60 queue_is "$EMAIL_QUEUE" messages 0
wait_for "the email for $order" 30 emails_are "$order" 1
say "Mailpit: $(emails "$order") email for order $order"
say "notifications.processed_messages: $(processed "$order") row for it"

step "3b. Delivered twice: the inbox stops a second email"
id=$(message_id "$order")
body=$(sql "$order" <<'SQL'
SELECT payload::text FROM orders.outbox_messages WHERE type = 'order.placed' AND payload->>'orderId' = :'order'
SQL
)
since=$(date -u +%Y-%m-%dT%H:%M:%SZ)
say "publishing message $id to $EMAIL_QUEUE again, as a redelivery after a crash would"
routed=$(curl -fsS -u "$RABBIT_AUTH" -H 'Content-Type: application/json' "$RABBIT/api/exchanges/%2F/amq.default/publish" \
  -d "$(jq -n --arg id "$id" --arg body "$body" --arg queue "$EMAIL_QUEUE" '{
    properties: {message_id: $id, type: "order.placed", content_type: "application/json", delivery_mode: 2},
    routing_key: $queue, payload: $body, payload_encoding: "string"}')" | jq -r .routed)
[[ $routed == true ]] || fail "RabbitMQ didn't route the copy to $EMAIL_QUEUE"
handled_before() { worker_log "$since" | grep -q "Message $id was handled before"; }
wait_for "the worker to handle the copy" 30 handled_before
wait_for "$EMAIL_QUEUE to empty" 30 queue_is "$EMAIL_QUEUE" messages 0
say "worker: $(worker_log "$since" | grep -o "Message $id was handled before.*" | head -n 1)"
say "Mailpit: still $(emails "$order") email for order $order"
say "notifications.processed_messages: still $(processed "$order") row for it"
