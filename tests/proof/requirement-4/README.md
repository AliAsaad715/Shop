# Requirement 4 proof (task R4-10)

Six steps show what Requirement 4 asks for: orders that never wait for email, emails that are never lost, and pages
that update without a refresh. Four scripts run them against the Compose stack and print what happened. Each script
puts the stack back as it found it, even when a step fails. [`results/`](results) holds the output of a real run.

| Interview step | Script | What you see | The automated test that proves the same |
| --- | --- | --- | --- |
| 1. Stopped worker | [`1-stopped-worker.sh`](1-stopped-worker.sh) | Three orders return 201 at once; the email queue holds three messages and no consumer | `OrderConfirmationDeliveryTests.StoppingTheWorkerAndRestartingTheBrokerKeepsEveryConfirmationEmail` |
| 2. Restart | the same script | The queue empties and Mailpit has the three emails | the same test |
| 3. Crash mid-send | [`2-crash-mid-send.sh`](2-crash-mid-send.sh) | The killed worker's message comes back with `x-delivery-count` 1, and the email arrives once. The same message sent again sends nothing | `OrderConfirmationDeliveryTests.AConnectionLostAfterRecordingButBeforeAckRedeliversWithoutSendingAgain` |
| 4. Mail failure | [`3-mail-failure.sh`](3-mail-failure.sh) | A wrong password fails five deliveries and parks the message in the DLQ; fixed and moved back, the email arrives once | `OrderConfirmationDeliveryTests.FiveFailedDeliveriesMakeFifteenSmtpAttemptsAndDeadLetterTheMessage` |
| 5. Admin dashboard | [`4-live-pages.sh`](4-live-pages.sh) prints what to do in the browser | A customer's new order appears at once | `R409EndToEndTests.AnHttpOrderIsSavedAndPublishedByTheBackgroundRelayToRabbitMqAndTheAdmin` |
| 6. Customer page | the same script | The admin's change appears at once for its customer only; the admin hub refuses a customer's token (403) | `R409EndToEndTests.HttpStatusChangesReachTheirOwnerWithoutLeakingToAnotherWorkingConnection`, `SignalRSecurityTests` |

Why the design works this way is in [ADR 0001](../../../docs/adr/0001-transactional-outbox-and-at-least-once-delivery.md);
questions to practise are in [the mock interview](../../../docs/interview/requirement-4.md).

## Before you start

The scripts need Docker, `curl` and `jq`. Run them from anywhere; they find the repository root themselves.

1. A RabbitMQ volume created before R4-09 still has the email queue with `x-delivery-limit` 5, which the new API and
   worker can't declare (the scripts stop with a message if so). Check it, and upgrade it once if it says 5:

   ```sh
   curl -s -u shop:shop localhost:15672/api/queues/%2F/email.order-confirmation | jq '.arguments."x-delivery-limit"'
   docker compose stop api1 api2 api3 # nothing new is published
   # wait until the RabbitMQ UI shows email.order-confirmation with 0 messages, then:
   docker compose stop worker
   docker compose exec rabbitmq rabbitmqctl delete_queue email.order-confirmation --if-empty --if-unused
   ```

   `--if-empty --if-unused` makes RabbitMQ refuse if a message or a consumer is still there. The README's "Upgrading
   the email queue to five deliveries (R4-09)" explains each check.

2. Build the API and the worker from this code, then start everything:

   ```sh
   docker compose build api1 worker   # api1 builds the image all three API instances use
   docker compose up -d --wait
   ```

Each script makes its own customer, so the scripts can run again and in any order. Sign-in allows 5 tries a minute
(`LOGIN_RATE_LIMIT_PERMIT_LIMIT`): if a script says there were too many sign-ins, wait a minute.

## 1 and 2. Stopped worker, then restart

```sh
tests/proof/requirement-4/1-stopped-worker.sh
```

It stops the worker, places three orders, and shows them waiting in RabbitMQ. Then it starts the worker and waits
for the queue to empty and for the three emails. Open the RabbitMQ UI (Queues and Streams) and Mailpit while it runs
to show the same numbers there.

```text
   order 1: 201 in 0.01 s (01a1f244-057e-7bfb-8caa-e82617e74860)
   ...
   RabbitMQ (http://localhost:55673, Queues and Streams): email.order-confirmation has 3 messages ready and 0 consumers
   Mailpit: 0 emails to worker-1795065905@shop.test
   $ docker compose start worker
   after 10 s: email.order-confirmation has 0 messages and 1 consumer
   Mailpit (http://localhost:55825): 3 emails to worker-1795065905@shop.test:
```

## 3. Crash mid-send

```sh
tests/proof/requirement-4/2-crash-mid-send.sh
```

Pausing Mailpit makes the send hang: the worker connects and waits for the mail server's greeting. The script places
an order, waits until the worker holds the message unacknowledged, and kills the worker (`SIGKILL`, no graceful
stop). RabbitMQ gets the message back and counts the lost connection as a failed delivery. Started again with
Mailpit unpaused, the worker sends the email once.

Then it publishes the same message (same message ID, same body) to the email queue again, as RabbitMQ would after a
crash between recording and acknowledging. The worker finds the ID in `notifications.processed_messages` and
acknowledges it without sending:

```text
   RabbitMQ: 1 ready again, x-delivery-count 1: the lost connection returned it and counted a failed delivery
   Mailpit: 1 email for order 01a1f244-50d3-70f3-8471-f842dfdb91e0
   worker: Message 01a1f244-50d7-738c-a774-ee0940ee2c9e was handled before; acknowledging it without sending again
   Mailpit: still 1 email for order 01a1f244-50d3-70f3-8471-f842dfdb91e0
```

One duplicate is still possible, and worth saying in the interview: if the worker dies after the mail server
accepted the email and before it recorded the ID, the email goes out twice, with the same Message-ID.

## 4. Mail failure

```sh
tests/proof/requirement-4/3-mail-failure.sh
```

[`compose.mail-failure.yaml`](compose.mail-failure.yaml) makes Mailpit ask for a password, as a real provider does,
and switches the worker to `Email:Provider=Smtp`. With a wrong password, every delivery fails as a settings error,
which the worker hands straight back to RabbitMQ. After five deliveries (the first try plus four retries) RabbitMQ
moves the message to the DLQ:

```text
     535: 5.7.8 Authentication credentials invalid, after 0 failed deliveries
     ...
     535: 5.7.8 Authentication credentials invalid, after 4 failed deliveries
   RabbitMQ: email.order-confirmation.dlq has 1 message; this one is 01a1f244-c026-7248-8c32-c9bd6dbfc345, x-first-death-reason delivery_limit
```

Then it fixes the password, moves the message back with the shovel from the README's R4-05 section, and the email
arrives once; the shovel deletes itself. At the end Mailpit and the worker go back to the settings in `.env`.
Recreating Mailpit empties its inbox, and the move back takes everything in the DLQ, not just this run's message.

To show it with a real provider instead, follow "The interview's mail failure, step by step" in the README's R4-05
section, with `EMAIL_PROVIDER=Smtp` and a wrong `SMTP_PASSWORD` in `.env`.

## 5 and 6. The live pages

```sh
tests/proof/requirement-4/4-live-pages.sh
```

It creates two customers, Alice and Bob, and prints what to do in three browser tabs: the admin's dashboard, Alice's
page and Bob's page. Each tab keeps its own sign-in. In the browser:

- Alice places an order: the dashboard shows it at once and logs `OrderPlaced`.
- The admin clicks Confirmed: Alice's page shows it at once and logs `OrderStatusChanged`; Bob's stays empty.

Then it checks, without a browser, that the admin hub refuses Alice's token both ways a client can connect:

```text
   Alice's token, negotiate with /hubs/admin: 403
   Alice's token in the URL, WebSocket to /hubs/admin: 403
   the admin's token, negotiate with /hubs/admin: 200
   the admin's token in the URL, WebSocket to /hubs/admin: 101
   Alice's token, negotiate with /hubs/orders: 200
```

## How the output in `results/` was made

On a separate Compose project (`COMPOSE_PROJECT_NAME=shop-r410-proof`), so its ports differ from the defaults: the
API on 55080, the RabbitMQ UI on 55673 and Mailpit on 55825. The API and worker were built from this branch, on
19 November 2026. The same run checked steps 5 and 6 in headless Chromium: Alice's page showed
Confirmed 0.10 s after the admin's click, Bob's page received nothing, every tab reloaded on `ResyncRequired` after
the API's RabbitMQ connection was closed, and no page logged a script error or a CSP violation.
