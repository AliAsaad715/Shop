# ADR 0002: least connections and shared state for three API instances

Status: accepted for the Requirement 5 deployment.

## Context

The same API image runs as api1, api2 and api3. A cached catalogue read is short, an order may wait for the
warehouse/database, and a SignalR WebSocket remains open for minutes. Request counts alone do not describe the
work held by an instance. A single source IP is also normal in a k6 run and behind a client network gateway.

## Decision

Use Nginx `least_conn`, with a shared `zone api 64k`, instead of round robin or client-IP affinity. Keep the
three named upstreams, Docker DNS resolution (`valid=10s`, `resolver_timeout 5s`), passive health checks
(`max_fails=1 fail_timeout=10s`) and a two-second connect timeout in `deploy/nginx/default.conf`.

Each request can reach any API. JWT validation and the persistent database are shared; the Redis login quota
is supplied by R5-06. Product details use Redis without a local copy (R5-08). S3-compatible image storage is
supplied by the existing R5-07 branch. SignalR uses WebSockets with skipped negotiation (R5-10); an instance's
event queue pushes to its own live connections. Reconnection reloads state. No sticky sessions are required.

Nginx retries connection errors/timeouts at another upstream, with at most two attempts. Do not enable
`non_idempotent`: a POST that reached an instance must not be replayed by the proxy. Abruptly losing an instance
can still cut off an in-flight request; the proof's near-zero error threshold is not an exactly-once guarantee.

## Consequences

Open connections, including WebSockets, influence placement. A short, homogeneous read run should be roughly
even, but least connections does not promise an exact third of requests or measure CPU directly. `X-Instance`
counts and Nginx's upstream access log provide independent observations.

Passive checks learn from real requests, so the first request after a failure may pay the connect timeout.
Dynamic resolution lets a recreated container return without a reload. The continuity proof stops api2 during
k6 traffic and requires it to answer again within ten seconds after its listener returns.

More instances do not multiply one laptop's CPU, memory or database capacity. R5-09 reserves the primary's
capacity for both write and failover read pools, and leaves room for workers and tools. The demo-data comparison
is a repeatable continuity check, not a claim of a threefold capacity improvement. Requirement 3's seeded
snapshot, rate and repeated runs are needed for the capacity comparison.

Nginx, Postgres, Redis, RabbitMQ and the object-storage service remain single points of failure. The single-node
RabbitMQ quorum queue is durable, not replicated. Their high availability is outside these twelve tasks.

## Evidence

Run `tests/proof/requirement-5/before-after-checks.sh` and `scalability-checks.sh`. The Requirement 5 workflow
uploads the exact commit, per-instance counts, error rates, latency percentiles, stop/rejoin events, connection
budgets/peaks and the before/after test results. See the proof README for the existing checks and pending PRs.
