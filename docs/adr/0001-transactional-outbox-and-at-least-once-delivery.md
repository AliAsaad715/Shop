# ADR 0001: Transactional outbox and at-least-once delivery for order events

- **Status:** Accepted
- **Date:** 19 November 2026. Decided in the Requirement 4 plan (13 November); built in tasks R4-01 to R4-09.
- **Scope:** the events an order sends after it is saved: the confirmation email and the live updates.

## Context

Requirement 4 asks for three things: placing an order never waits for email or live updates, the email goes
through a queue to a worker that runs on its own, and the admin and customer pages update without a refresh.

Saving an order writes to PostgreSQL. Telling the rest of the system writes to RabbitMQ. These are two systems with
no transaction in common, so whichever order the code writes them in, a crash between the two writes breaks
something:

| Order of the writes | Crash or outage between them | Result |
| --- | --- | --- |
| Commit the order, then publish | The API stops, or RabbitMQ is down | The order exists, but nothing remembers its event: no email, no live update, ever |
| Publish, then commit the order | The commit fails, for example a status change that loses the version race (409) | An email confirms an order that was never saved |

A distributed transaction (two-phase commit) can't include RabbitMQ, and it would make every order wait for the
broker anyway. Networks also fail after a message is sent and before its receipt is acknowledged, so some message
will sometimes arrive twice. The email itself goes to an SMTP server, outside any transaction we control.

## Decision

**1. A transactional outbox.** `Order.Place` and `Order.MoveTo` record an event inside the order. When the order is
saved, `OrdersDbContext` turns each event into a row of `orders.outbox_messages` in the same `SaveChanges`, so one
transaction commits the order and its event, or neither. The row goes in the same batch as the order, so placing an
order is still one database round trip. The request returns 201 at this point; it never waits for RabbitMQ.

**2. A relay publishes the rows.** A background service in the API wakes as soon as a save commits, and polls every
second in case it missed a wake-up or the API restarted. One pass, in one transaction:

1. Locks up to 50 unpublished rows, oldest first, with `FOR UPDATE SKIP LOCKED`. A second API instance skips those
   rows instead of waiting for them, so two relays never publish the same row at the same time.
2. Publishes each row as a persistent message whose message ID is the row's ID.
3. Waits for RabbitMQ's publisher confirms, and sets `published_at` only on the confirmed rows. A row that wasn't
   confirmed keeps `attempts` and `last_error` and is tried again. After a failed pass the relay waits 1 s, doubling
   up to 30 s. Published rows are deleted after 7 days.

`order.placed` is published twice with the same message ID: a mandatory copy addressed straight to the
`email.order-confirmation` queue, and a copy to the `shop.events` topic exchange for the live pages. Both must be
confirmed before the row counts as published, so a missing email queue keeps the row waiting instead of losing the
email.

**3. At-least-once delivery, with consumers that tolerate repeats.** We don't promise exactly-once, which can't
reach across an SMTP server. Instead, every step may deliver a message again, and every consumer handles a repeat:

| Where a repeat can come from | Why | What stops it from causing harm |
| --- | --- | --- |
| The relay | RabbitMQ confirmed a batch, then the API stopped before the commit | The message ID is the row ID, the same each time |
| RabbitMQ | A consumer's connection dropped before its acknowledgement | Same message ID |
| The email worker | It sends, records the ID in `notifications.processed_messages`, then acknowledges. It skips an ID it has recorded | A crash between sending and recording costs one duplicate email, never a lost one; both copies carry the same Message-ID header |
| The live-update consumer | Each API instance has its own exclusive queue and must get its own copy | It remembers sent IDs for ten minutes, and the pages ignore an event whose version isn't newer than the one shown |

**4. Every failure ends with the email sent or parked, never silently gone.** The email queue is a quorum queue with
`x-delivery-limit` 4: five deliveries in all, then RabbitMQ moves the message to `email.order-confirmation.dlq`.
The worker sorts each failure:

- **Temporary**, such as the mail server down: three tries in the worker, then `basic.reject` with requeue.
- **Permanent**, such as an address that doesn't exist: `basic.reject` without requeue, straight to the DLQ.
- **Our settings**, such as a wrong password: back to RabbitMQ at once, counted, without retrying in the worker.

It retries with `basic.reject`, never `basic.nack`. Since RabbitMQ 4.3 only a reject with requeue and a lost
connection count toward the limit; a nacked email would retry forever. The worker nacks only when it stops: messages
it never started, and one cut off when the 20-second graceful stop runs out, so a restart never uses up a try. After
the cause is fixed, the shovel in the README moves the DLQ's messages back. They keep their message IDs, so one
already sent isn't sent again.

## Consequences

Good:

- A saved order always gets its event, even if RabbitMQ is down for an hour or the API stops right after the commit.
- Placing an order doesn't depend on RabbitMQ, the worker or the mail server; `/health` reports Degraded instead.
- Stopping the worker loses nothing: the messages wait in a durable queue.
- Several API instances can run the relay together, which Requirement 5 needs.

Costs:

- One more row in the order's transaction, in the same round trip, and a relay that keeps polling. The plan's load-test
  comparison (`perf/run.sh after-outbox`) hasn't been run yet.
- Events reach consumers a moment after the order is saved: right away normally, up to 30 s after a long outage.
- Every consumer has to tolerate repeats, and the email worker needs its own inbox table.
- One duplicate email is still possible, in the moment between the SMTP server accepting it and the worker recording
  it. We accept that over a lost confirmation.
- The outbox table needs cleanup, which the relay does after 7 days.

## Alternatives considered

| Option | Why we didn't choose it |
| --- | --- |
| Publish right after the commit | Loses the event when the API stops or RabbitMQ is down in between |
| Two-phase commit across Postgres and RabbitMQ | RabbitMQ doesn't take part in one, and every order would wait for the broker |
| Change data capture (Debezium reading the write-ahead log) | Another service and connector to run, for one table |
| Postgres `LISTEN/NOTIFY` to wake the relay | An in-process signal is enough while the relay runs in the API that saved the order; the poll covers the rest |
| MassTransit's outbox | It hides the guarantees the interview asks about, and MassTransit moved to a commercial license with v9 |
| Exactly-once delivery | Can't extend across an SMTP server; idempotent consumers give the same result for what we control |
| `basic.nack` to retry | Since RabbitMQ 4.3 it doesn't count toward the delivery limit, so a broken email would retry forever |

## How we know it works

| Claim | Automated test | Proof kit step |
| --- | --- | --- |
| The order and its outbox row commit together, or neither | `OutboxTests` | |
| An order returns 201 while RabbitMQ is down, and its event goes out after recovery | `R409EndToEndTests.AnHttpOrderReturns201WhileRabbitMqIsDownAndItsOutboxPublishesAfterRecovery` | |
| Two relays never publish the same row at once | `OutboxTests` | |
| A stopped worker loses nothing | `OrderConfirmationDeliveryTests.StoppingTheWorkerAndRestartingTheBrokerKeepsEveryConfirmationEmail` | Steps 1 and 2 |
| A crash before the acknowledgement brings the message back without a second email | `OrderConfirmationDeliveryTests.AConnectionLostAfterRecordingButBeforeAckRedeliversWithoutSendingAgain` | Step 3 |
| Five failed deliveries park the email in the DLQ, and moving it back sends it once | `OrderConfirmationDeliveryTests.FiveFailedDeliveriesMakeFifteenSmtpAttemptsAndDeadLetterTheMessage`, `ConsumerTests.MovingTheDlqBackGivesTheMessageAllItsTriesAgain` | Step 4 |

The proof kit is in [tests/proof/requirement-4](../../tests/proof/requirement-4/README.md). The details of each part
are in the README's sections for tasks R4-01 to R4-09.
