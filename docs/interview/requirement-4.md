# Requirement 4 mock interview: async queues and real-time updates

Questions an interviewer is likely to ask about Requirement 4, with short answers. Each answer ends with what to show:
a file, a test, or a step of the [proof kit](../../tests/proof/requirement-4/README.md). Practise saying each answer
in your own words in under a minute, then show the proof. The reasons behind the big decision are in
[ADR 0001](../adr/0001-transactional-outbox-and-at-least-once-delivery.md).

## The 60-second summary

"Placing an order saves the order and an outbox row in one transaction and returns 201. That's the only thing the
customer waits for. A relay in the API publishes the row to RabbitMQ and marks it sent only after RabbitMQ confirms
it. From there, two things happen independently. A separate worker container takes the email copy from a durable
quorum queue and sends the confirmation over SMTP; failures are retried and, after five deliveries, parked in a
dead-letter queue. And each API instance takes the live copy from its own temporary queue and pushes it over SignalR:
new orders to the admins, status changes only to the order's own customer. Delivery is at least once, so every
consumer is built to handle a message twice."

## The flow

**1. Walk me through what happens when a customer places an order.**
The API saves the order and an `order.placed` outbox row in one transaction and returns 201. The relay wakes at
once, publishes the row with publisher confirms, and marks it published. RabbitMQ puts one copy in the email queue
and one in every API instance's live queue. The worker sends the email; the admin dashboard gets `OrderPlaced`.
*Show:* the "At a glance" diagram in the plan, then proof steps 1, 2 and 5.

**2. Why a separate worker container, and not a background service in the API?**
The brief asks for an independent worker. A separate container can stop, crash or scale without touching the API:
orders keep returning 201 and their emails wait in the queue. It only registers the Notifications module, so it
can't read the Orders or Users tables; everything the email needs is in the message.
*Show:* proof steps 1 and 2: three orders return 201 with the worker stopped, and the queue shows three messages.

## The outbox

**3. Why not just publish to RabbitMQ right after `SaveChanges`?**
Because those are two systems with no shared transaction. If the API stops or RabbitMQ is down between the commit
and the publish, the order exists but its event is gone for good: no email, ever. Publishing first is worse: the
commit can still fail, and then the email confirms an order that doesn't exist. The outbox turns the publish into a
database write in the same transaction, so a saved order always has its event.
*Show:* the table at the top of the ADR; `OutboxTests` (an order and its row commit together, or neither).

**4. What does the outbox cost?**
One more row in the same transaction. It goes in the same batch as the order, so it's still one database round trip.
The relay's work happens after the 201. A partial index covers only unpublished rows, so the relay's search stays
small, and published rows are deleted after 7 days. The plan's load-test comparison hasn't been run yet, so I'd say
that rather than quote a number.

**5. You run two API instances. How do two relays avoid publishing the same row?**
Each pass locks up to 50 unpublished rows with `FOR UPDATE SKIP LOCKED` in one transaction. A second relay skips the
locked rows instead of waiting for them, and takes the next ones.
*Show:* `OutboxTests` (a second relay skipping locked rows without waiting).

**6. Then why do the consumers still need to handle duplicates?**
`SKIP LOCKED` stops two relays publishing a row *at the same time*. It doesn't stop a repeat after a crash: if
RabbitMQ confirms a batch and the API stops before the commit, those rows are still unpublished after the restart and
go out again. RabbitMQ also redelivers any message whose acknowledgement was lost. That's at-least-once delivery.

**7. Why wait for publisher confirms?**
A publish without a confirm only means the bytes left our process. A confirm means RabbitMQ has stored the message,
and only then is the row marked published. No confirm within 5 seconds, or a nack, and the row stays unpublished and
is retried.
*Show:* `PublisherTests`.

**8. Why is `order.placed` published twice?**
One copy goes to the email queue by its exact name, with the mandatory flag. The other goes to the `shop.events`
topic exchange for the live pages. If the email queue were missing, a mandatory publish comes back unroutable and the
outbox row stays waiting. With only the topic exchange, the live queues would accept it and hide the fact that the
email queue is gone. Both copies must be confirmed before the row counts as published.

## Delivery guarantees

**9. Is your system exactly-once?**
No. Exactly-once can't reach across an SMTP server: the server can accept an email and the connection can drop before
we hear about it. We deliver at least once and make every consumer handle a repeat. The worker keeps an inbox,
`notifications.processed_messages`, keyed by message ID. The live consumer remembers sent IDs for ten minutes, and the
pages ignore an event that isn't newer than what they show.

**10. The worker sends the email, then records the message ID. Why that order?**
Recording first and then crashing before the send would lose the email forever: the next delivery sees the ID and
skips it. Sending first means a crash between the send and the record costs one duplicate email. A duplicate is the
lesser harm, and both copies carry the same Message-ID header, so a mail client can tell they're one message.
*Show:* proof step 3b: the same message delivered a second time, and the worker logs "was handled before".

**11. Where does the message ID come from?**
It's the outbox row's ID. It stays the same however many times the relay or RabbitMQ sends the message, and moving a
message back from the DLQ keeps it too. That's what makes the inbox work.

## RabbitMQ

**12. Why a quorum queue for the email?**
It's durable and replicated, and RabbitMQ counts each message's failed deliveries itself. With `x-delivery-limit` 4
a message gets five deliveries in all, and then RabbitMQ moves it to the dead-letter queue. RabbitMQ 4 would otherwise
default to a limit of 20 and then drop the message if no DLQ is set, so both are set explicitly.
*Show:* `TopologyTests`; the queue's arguments in the management UI.

**13. Why does the worker retry with `basic.reject`, never `basic.nack`?**
Since RabbitMQ 4.3, only a reject with requeue and a lost consumer connection count toward the delivery limit. A nack
with requeue doesn't, so a broken email that's nacked would retry forever and never reach the DLQ. The worker nacks
only when it stops: messages it never started, and one cut off when the 20-second graceful stop runs out. So a
restart never uses up a try.
*Show:* `PublisherTests.NackWithRequeueNeverCountsTowardsTheDeliveryLimit`.

**14. What's your prefetch, and why?**
Five, with manual acknowledgements and one message handled at a time. A crash returns at most five messages to the
queue, and other workers can take the rest instead of one worker holding them all.

**15. The worker crashes in the middle of sending. What happens?**
The message was never acknowledged, so when the connection drops RabbitMQ puts it back and counts one failed
delivery. The restarted worker gets it again and sends the email, once. If the crash came after the inbox record but
before the acknowledgement, the next delivery finds the ID and acknowledges without sending.
*Show:* proof step 3; `OrderConfirmationDeliveryTests.AConnectionLostAfterRecordingButBeforeAckRedeliversWithoutSendingAgain`.

**16. How do you treat different email failures?**
The worker sorts them. Temporary, like the mail server being down: three tries in the worker, then a reject with
requeue, so RabbitMQ counts it and redelivers. Permanent, like an address that doesn't exist: a reject without requeue,
straight to the DLQ, because trying again sends the same thing. Our own settings, like a wrong password: back to
RabbitMQ at once without retrying in the worker, because repeated failed sign-ins can lock the account.
*Show:* `EmailFailuresTests`; the table in the README's R4-05 section.

**17. Walk me through the DLQ.**
After five failed deliveries the message is in `email.order-confirmation.dlq`, with `x-first-death-reason` saying
why. You look at it in the management UI, fix the cause, then move the messages back with a shovel. A moved message
keeps its message ID, so one that was already sent isn't sent again, and it gets five new deliveries.
*Show:* proof step 4: a wrong password, five logged failures, the DLQ, then the email after the move back.

**18. What happens when RabbitMQ is down?**
Orders still return 201: the outbox rows wait, `/health` reports Degraded, and the relay retries with a backoff from
1 to 30 seconds. When RabbitMQ comes back, the client reconnects and declares the topology again, the relay publishes
the waiting rows, and the emails go out.
*Show:* `R409EndToEndTests.AnHttpOrderReturns201WhileRabbitMqIsDownAndItsOutboxPublishesAfterRecovery`.

## Live updates and SignalR

**19. How does a status change reach the right customer, and only them?**
The status-change event carries the customer's ID. The API sends it with `Clients.User(customerId)` on the orders
hub, and SignalR's user ID comes from the validated token's `sub` claim through our `IUserIdProvider`. There's no
"subscribe to order X" method a client could abuse; a customer only ever gets their own orders.
*Show:* proof step 6: Alice sees Confirmed at once, Bob's page stays empty.

**20. How do you stop a customer from opening the admin hub?**
The admin hub requires the `AdminOnly` policy, so a customer's token is refused before the connection opens, both on
negotiate and on a direct WebSocket.
*Show:* proof step 6b: 403 for Alice, 200 and 101 for the admin.

**21. Browsers can't send headers on a WebSocket. How do you authenticate it?**
The SignalR client sends the token as `access_token` in the query string. The API reads it only for paths under
`/hubs` and validates it exactly like a header token. Query strings stay out of the logs, and in production this
runs over HTTPS. `CloseOnAuthenticationExpiration` closes a connection when its token expires.

**22. With several API instances, do you need a Redis backplane for SignalR?**
Not for these pushes. Each instance declares its own exclusive, auto-delete queue bound to `order.*`, so every
instance gets its own copy of every event and pushes it to the connections it holds. A backplane would only be
needed for messages that start inside one instance.
*Show:* `RealtimeDeliveryTests.EachInstanceHasItsOwnExclusiveQueueAndReceivesTheSameMessage`.

**23. Events can arrive late, twice or out of order. How do the pages cope?**
Every event carries the order's version. A page applies an event only if its version is newer than the one shown, and
logs the rest as ignored. A page connects first and loads second, so nothing in between is missed, and a fresh list
is merged by version, so it never takes a row back to an older state.

**24. What happens to a page when its connection drops?**
SignalR reconnects by itself, and the page reloads its list, because events sent while it was away aren't replayed.
The server also sends `ResyncRequired` whenever its own RabbitMQ subscription comes back, so pages reload even if
their WebSocket never dropped.

## Testing and a story

**25. How did you test all this?**
With real infrastructure in containers. Testcontainers starts RabbitMQ 4.3 and Postgres 18, and the end-to-end tests
run the real API program: HTTP order, outbox, relay, RabbitMQ, the live consumer and a SignalR client. Only the
outside boundaries (catalog, warehouse, SMTP) are replaced. A GitHub workflow runs the suites on every pull request.
*Show:* the table in the README's R4-09 section.

**26. Tell me about a bug you found.**
Checking the live pages in a real browser, I closed the API's RabbitMQ connection to test recovery. Everything
recovered, but RabbitMQ's management API showed two live queues for one API, and one more after each loss. The extra
ones had no consumer and kept a copy of every order event until the API stopped. The cause was two owners for one
subscription: after a connection loss, the RabbitMQ client restored the old queue by itself, and our consumer, which
saw its consumer unregistered, also built a new one. I wrote a test that drops the connection three times and expects
exactly one live queue. It failed with two, and after a one-line fix (replace the subscription only while the
connection is open) it passed. The lesson: give each resource exactly one owner.
*Show:* `RealtimeDeliveryTests.RecoveryNeverLeavesALiveQueueWithoutAConsumer`.

**27. What would you improve next?**
Run the load-test comparison for the outbox. Alert on the DLQ's depth and on the age of the oldest unpublished outbox
row, which are the two numbers that show something stuck. Use a real provider with TLS in production. For
Requirement 5's extra instances, the relay and the live queues already work as they are.

## Traps to watch for

- **"So nothing is ever sent twice?"** No: at least once. One duplicate email is possible if the worker crashes after
  SMTP accepted the email and before it recorded the ID. Say so plainly.
- **"Five retries?"** Five *deliveries*: the first try plus four retries. The worker also tries up to three times
  inside each delivery for a temporary failure, so up to 15 SMTP attempts before the DLQ.
- **"Are events in order?"** Within a relay pass, oldest first. A row that fails is retried later, so a later event
  can overtake it. That's why status events carry a version.
- **"Does `basic.nack` count as a failure?"** Not since RabbitMQ 4.3. Only a reject with requeue and a lost connection
  do.
