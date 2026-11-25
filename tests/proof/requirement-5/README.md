# Requirement 5 proof kit (R5-12)

Use Linux/WSL with Docker, the .NET SDK pinned by `global.json`, jq and curl. Initialize the warehouse submodule.
The two new runners use disposable databases/brokers; the load runner creates its own Compose project, images,
network and volumes and removes them on exit. It does not stop or change the developer's Compose stack.
Mailpit is forced on, and test addresses never go to an external SMTP service.

```sh
git submodule update --init
bash tests/proof/requirement-5/before-after-checks.sh
bash tests/proof/requirement-5/scalability-checks.sh
```

The first command requires clean versions of the two source files it checks. It temporarily replaces each
with its tagged pre-fix implementation, requires the specific regression to fail, restores the source through
an EXIT trap, and requires the same check to pass. Other failures (build, Docker, etc.) are not accepted as proof.

The load runner:

1. Runs three real production worker registrations on RabbitMQ/Postgres. Thirty events reach all three workers;
   prefetch is read from RabbitMQ and must be one. Replayed IDs send no additional mail. A worker lost during a
   held send gives its unacknowledged message to the other two; the existing post-record/pre-ack test covers dedup.
2. Starts all three APIs, three workers, the migrator, Nginx and shared services, including the physical replica.
3. Checks the effective pool sizes and `SHOW max_connections` on both servers. It also rejects the tagged
   unbounded configuration and a deliberately oversized pool. Connection strings/passwords are not printed.
4. Sends exactly 500 k6 requests to `http://nginx`, counting the `X-Instance` response header. Every named instance
   must answer. Counts/percentages are reported; exact thirds are not a property of least connections.
5. Keeps k6 running while api2 stops. Thirty additional probes must be served by api1/api3. After restarting api2,
   its first successful k6 response must be within ten seconds of its listener returning, measured from log
   timestamps at one-second resolution. The original listener/response timestamps are retained. Error rate must remain below 1%, with no dropped
   k6 iterations. In-flight requests from a hard kill can fail; the runner uses a graceful stop.
6. Runs at the same rate with only api1 and with all three, on the same six demo products. It records p50/p95/p99,
   error rates and connection peaks on both servers, rejecting exhaustion errors or loss of the ten-connection
   headroom. This small cached-read comparison is not the Requirement 3 capacity benchmark.

`RATE` changes the comparison/continuity rate (100 requests/s by default). `RESULTS_DIR` changes the artifact folder
(`artifacts/requirement-5/` by default). Artifacts include `comparison.md`, k6 JSON/text, stop/rejoin times,
connection budgets/samples/peaks, worker TRX output, and container logs, including on failure. The GitHub
**Requirement 5 proof** workflow runs these commands and uploads them as `requirement-5-proof`.

## Before and after, without rewriting history

The historical audit logs stay unchanged. Use the listed tag/implementation and the same check on the fixed code.

| Finding | Before | After/check | Existing implementation |
| --- | --- | --- | --- |
| Migration/seeding races | Failed first starts | One migrator, clean runtime starts; `compose-checks.sh` | R5-04 on main |
| Per-instance login quota | Fifteen attempts over three APIs | Five total; `RedisLoginRateLimitTests` | [PR #60](https://github.com/AliAsaad715/Shop/pull/60), merged into main and included in this branch |
| Nginx address used as client IP | One shared client bucket | Independent real clients; `forwarded-headers-checks.sh` | R5-05 on main |
| Local images | Three of nine reads succeed | Nine of nine, including imported/demo images | [PR #59](https://github.com/AliAsaad715/Shop/pull/59), merged into `feature/r5-05-forwarded-headers`, not main |
| Product-details L1 | Old body/ETag from warm peers | Nine immediate fresh reads across three warm hosts; `before-after-checks.sh` | R5-08 in this change; tag `r5-before-cache-coherence` |
| Unbounded DB pools | Up to 100 per pool/process | Worst-case primary 90, replica 30; actual server/budget checks | R5-09 in this change; tag `r5-before-connection-budget` |
| SignalR negotiation | Random cross-instance 404 | Direct WebSockets; reconnect reloads state; `nginx-checks.sh` | R5-10 on main |
| OpenAPI Host loses port | Server URL points at port 80 | Original host/port through Nginx; `nginx-checks.sh` | R5-02/R5-05 on main |
| Workers reserve a backlog | Prefetch five | Prefetch one, 30 shared emails, worker-loss/replay tests | R5-11 in this change; tag `r5-before-worker-prefetch` |
| Pending-release retries (audit finding 9) | Several APIs can retry one row | Still an explicitly recorded gap outside the original twelve tasks | See `docs/requirement-5/local-state-audit.md`; do not describe this retry job as already using SKIP LOCKED |

The SMTP send and the processed-message record are not one transaction. As [ADR 0001](../../../docs/adr/0001-transactional-outbox-and-at-least-once-delivery.md)
records, a crash after SMTP accepts an email but before the database records it can send twice. The worker proof
verifies the before-send and after-record failure points; it does not claim exactly-once SMTP delivery.

The existing `local-state-checks.sh`, `nginx-checks.sh`, `compose-checks.sh`, and
`forwarded-headers-checks.sh` retain their original checks. R5-07's `image-storage-checks.sh` is in its existing
branch. R5-06 is included through the merge of main. Integrate the already implemented R5-07 branch before
presenting a fully combined twelve-task deployment; this PR does not copy its changes.

## Full Requirement 3 comparison

Use a separate demo stack with the seeded 50,000-customer/500,000-order snapshot, as `perf/README.md` describes.
Use the same rate, dataset, cache/read-routing settings, commit and run count for both cases:

```sh
perf/seed.sh
perf/snapshot.sh save
API_INSTANCES=1 perf/run.sh one-instance 1000
API_INSTANCES=3 perf/run.sh three-instances 1000
LABELS=one-instance,three-instances perf/compare.sh one-instance
```

Do not replace the historical Requirement 3 results with the six-product smoke run. Report bottlenecks and noise
from the measured run, including the fact that all containers share one laptop. For the balancing decision and
remaining single points of failure, see [ADR 0002](../../../docs/adr/0002-least-connections-and-shared-state.md).

`perf/run.sh` reapplies `API_INSTANCES` after every snapshot restore and records it in the system JSON.
Stopping api2/api3 only before invoking the old runner was insufficient: its restore started all three again.
