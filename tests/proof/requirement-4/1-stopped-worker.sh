#!/usr/bin/env bash
# Interview steps 1 and 2 (task R4-10). With the email worker stopped, orders still return 201 at once and their
# confirmations wait in RabbitMQ. Started again, the worker sends every one of them, and the queue empties.
#
#   tests/proof/requirement-4/1-stopped-worker.sh
#
# It leaves the worker running at the end, even if a step fails.
source "$(dirname "$0")/lib.sh"
trap 'docker compose start worker >/dev/null 2>&1 || true' EXIT

docker compose start worker >/dev/null 2>&1
customer="worker-$(date +%s)@shop.test"
token=$(new_customer "$customer")
wait_for "the email queue to be empty before starting" 60 queue_is "$EMAIL_QUEUE" messages 0

step "1. Stopped worker: orders don't wait for email"
run docker compose stop worker
for n in 1 2 3; do
  read -r status seconds order < <(place_order "$token")
  [[ $status == 201 ]] || fail "order $n returned $status"
  say "order $n: $status in $seconds s ($order)"
done
wait_for "three messages ready in $EMAIL_QUEUE" 30 queue_is "$EMAIL_QUEUE" messages_ready 3
say "RabbitMQ ($RABBIT, Queues and Streams): $EMAIL_QUEUE has" \
  "$(plural "$(count "$EMAIL_QUEUE" messages_ready)" message) ready and" \
  "$(plural "$(count "$EMAIL_QUEUE" consumers)" consumer)"
say "Mailpit: $(plural "$(emails "to:$customer")" email) to $customer"

step "2. Restart: the worker sends them all"
started=$SECONDS
run docker compose start worker
wait_for "$EMAIL_QUEUE to empty" 60 queue_is "$EMAIL_QUEUE" messages 0
wait_for "three emails to $customer" 30 emails_are "to:$customer" 3
say "after $((SECONDS - started)) s: $EMAIL_QUEUE has 0 messages and" \
  "$(plural "$(count "$EMAIL_QUEUE" consumers)" consumer)"
say "Mailpit ($MAILPIT): $(plural "$(emails "to:$customer")" email) to $customer:"
while read -r subject; do say "  $subject"; done < <(subjects "to:$customer")
