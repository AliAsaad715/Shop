# Requirement 5: local-state audit (task R5-01)

Before Nginx spreads requests over three API instances, we looked for everything one instance keeps for itself:
anything that works with one instance and breaks with three. Each finding below has a check. The check fails on
today's code (the "before") and should pass once its task is done (the "after").

The audit found **9 problems** and **8 things that are already safe**, and it shows **8 places where the plan is
wrong or incomplete**. Every result below comes from a real run, not only from reading the code.

## How we did it

1. **Searched the code** for the things the plan lists, in the API, the modules, the worker and the browser pages:
   static fields, `ConcurrentDictionary` and other collections in fields, `IMemoryCache` and `HybridCache`, `lock`,
   `SemaphoreSlim` and channels, files on disk, `BackgroundService` and `IHostedService`, singletons, work done at
   startup (migrations, seeders), `RemoteIpAddress` and `Request.Host`, Data Protection, and the SignalR client.
2. **Read every hit** and sorted it: a problem, safe, or not used.
3. **Ran three API instances side by side** on one throwaway Postgres and one throwaway Redis, and checked each finding
   against them. There is no Nginx yet (R5-02), so every request goes to the instance it names, and each result is
   exact rather than "about a third". The script is
   [`tests/proof/requirement-5/local-state-checks.sh`](../../tests/proof/requirement-5/local-state-checks.sh), and
   [`results/`](../../tests/proof/requirement-5/results) holds the output of the run quoted here.

```sh
tests/proof/requirement-5/local-state-checks.sh
```

It needs Docker, the .NET SDK, `curl` and `jq`, on Linux or WSL, and takes about two minutes. It never touches the
Compose stack or its data: its two containers have their own names and ports, and RabbitMQ and the warehouse point at
a closed port, so the API runs Degraded without them, as designed.

## At a glance

| # | Finding | What goes wrong with three instances | What the run showed | Fixed by |
| --- | --- | --- | --- | --- |
| 1 | Migrations and seeding run in every instance at startup | Instances race on an empty database and crash | 6 of 9 first starts crashed | R5-04 |
| 2 | The login limiter counts in memory | Each instance allows its own 5 tries a minute | 15 tries before the first 429 | R5-06 |
| 3 | The limiter keys on the connection's address | Behind Nginx, every caller shares one bucket | before: client 2 starts at 429; after: independent 401 | R5-05 |
| 4 | Product images are on the local disk | An image exists only on the instance that saved it | 3 of 9 requests found the image | R5-07 |
| 5 | HybridCache keeps a local copy | Other instances serve an edited product's old data | old name for about 4–5 s | R5-08 |
| 6 | Each instance may open 100 database connections | Three can ask for 300; Postgres allows 100 | one instance already reached 99 | R5-09 |
| 7 | SignalR negotiates, then connects | The WebSocket can reach another instance and get 404 | 101 on api1, 404 on api2 | R5-10 |
| 8 | The OpenAPI `servers` URL comes from the Host header | With the plan's Nginx config, the docs point at port 80 | `http://localhost/` instead of `:8080` | R5-02 |
| 9 | Warehouse release retries don't claim their rows | Every instance retries the same rows at once | 15 of 20 rows retried by more than one | not in the plan yet |

## The problems

### 1. Migrations and seeding run in every instance at startup

**Where.** The API migrates every module before it starts
([`Program.cs:34`](../../src/Shop.Api/Program.cs#L34)), then two hosted services seed: the admin account
([`AdminSeeder.cs:34`](../../src/Modules/Users/Shop.Modules.Users/Seeding/AdminSeeder.cs#L34)) and the demo products,
which have fixed IDs ([`DemoProductSeeder.cs:67`](../../src/Modules/Products/Shop.Modules.Products/Seeding/DemoProductSeeder.cs#L67)).
The worker migrates the notifications schema the same way ([`Shop.Worker/Program.cs:13`](../../src/Shop.Worker/Program.cs#L13)).

**What goes wrong.** Three instances that start together on an empty database all find the work undone and all do
it. The losers crash. The API service has no restart policy, so a crashed instance stays down.

**What the run showed.** In three rounds, 6 of 9 first starts crashed, each with
`23505: duplicate key value violates unique constraint "EmailIndex"` while seeding the admin. The very first run
also hit a migration race: api1 and api3 both applied the orders module's `AddPendingReleases`, and api1 crashed
with `42P07: relation "pending_releases" already exists`
([excerpt](../../tests/proof/requirement-5/results/first-start-migration-race-20261121.txt)). The second start
always works (`No migrations were applied`, `Demo catalog already present`): it is a first-start problem.

**Why EF Core's migration lock didn't stop it.** EF Core 9 and later lock before migrating; with Npgsql 10.0.3 the
lock is `LOCK TABLE <history table> IN ACCESS EXCLUSIVE MODE`. The logs show api3 applying `AddPendingReleases` right
after api1 finished `InitialOrders`, while api1 went on to apply it too: the lock did not cover api1's whole run,
which fits PostgreSQL releasing a table lock when each migration's transaction commits.

**Fix: R5-04, the migrator.** Two things the plan doesn't mention:

- The migrator must also apply the **notifications** schema, and the workers must stop migrating, or three worker
  replicas race the same way. The API doesn't register `NotificationsDbContext`; only the worker does.
- The demo seed writes each product's image to the local disk. A migrator container without the images volume would
  leave every demo image missing. Mount the volume in the migrator until R5-07, or do R5-07 first.

**Before → after.** Before: check 1 shows crashes. After: on an empty database, `docker compose up` shows one
migrator log and three instances that start without migrating or seeding.

**Implemented in R5-04.** `--migrate-and-seed` applies all four schemas, then explicitly runs the admin and demo
seeders without starting the host. The migrator mounts the same images volume as the API. API instances and all
three workers depend on its successful completion, and neither API nor worker migrates or seeds at startup,
including in Development. Check 1 now runs the migrator before starting the three instances in every fresh-database
round; the saved pre-fix logs above remain the historical baseline. `compose-checks.sh` also checks a failed seed
blocks dependent hosts, retry succeeds on the same database, and runtime restarts do not initialize again.
The [saved R5-04 run](../../tests/proof/requirement-5/results/local-state-checks-r5-04-20261123.txt) had **0 crashes
out of 9 starts**, down from 6 of 9 before the fix. Other findings in that run remain for their own tasks.

### 2. The login rate limiter counts in memory

**Where.** [`LoginRateLimitPolicy.cs:22`](../../src/Modules/Users/Shop.Modules.Users/Auth/LoginRateLimitPolicy.cs#L22)
uses ASP.NET Core's in-process fixed-window limiter.

**What goes wrong.** Each instance keeps its own counter, so a client that lands on all three gets 15 tries a minute
instead of 5.

**What the run showed.** Ten wrong passwords to api1 alone: five 401s, then 429. Eighteen spread over api1–api3:
**fifteen 401s** before the first 429.

**Fix: R5-06**, counters in Redis.

**Before → after.** Check 2: before, 15 tries before the first 429; after, 5, wherever each try lands.

**Implemented in R5-06.** The policy now uses an asynchronous Redis limiter; its Lua script atomically acquires
a permit and sets the first request's expiry. All instances use the same normalized client-IP key. A restarted
API keeps the quota, denied requests do not extend it, and Redis failure returns 503 rather than granting a local
allowance. Compose uses `noeviction` so catalog pressure cannot discard active quotas. The pre-fix commit is
tagged `r5-before-login-rate-limit`; `RedisLoginRateLimitTests` covers three independent hosts and concurrent
acquisitions. The historical measurements above are retained; this entry describes the implementation and tests.

### 3. The limiter keys on the connection's address

**Where.** [`LoginRateLimitPolicy.cs:15`](../../src/Modules/Users/Shop.Modules.Users/Auth/LoginRateLimitPolicy.cs#L15)
takes `Connection.RemoteIpAddress` and, on purpose, ignores forwarding headers a client could fake.

**What goes wrong.** Behind Nginx, every request comes from Nginx's address, so all callers share one bucket: five
wrong passwords from anyone block every login for a minute.

**What the R5-05 run showed.** With two different client-container IPs and one API behind Nginx, client 1 got five
401s followed by a 429. Before the fix, client 2's very first request also got 429; after it, client 2 got its own
401. Changing forged forwarding headers could not bypass the limit. One API keeps this proof independent of
R5-06's separate cross-instance counter problem.

**Fix: R5-05.** `UseForwardedHeaders`, trusting `X-Forwarded-For` only from the Docker network Nginx is on, so a
client still can't choose its own address by sending the header itself.

**Before → after.** Log the limiter's key: before, Nginx's address for every caller; after, the caller's. From the
host, Docker usually shows every client to Nginx as the network's gateway address, so a demo with two different
clients needs one of them to call from inside a container.

**Implemented in R5-05.** The API accepts one hop of client address and scheme from the explicitly configured
Docker subnet only, before security and rate limiting. Nginx replaces incoming forwarding values with its own
observations in both API and hub locations. The original Host/port stays intact, and untrusted forwarded hosts
are ignored. Each login attempt logs the same normalized client IP used for its quota. The network CIDR and API
trust range share one Compose variable; direct local runs disable forwarding by default. The proof is
[`forwarded-headers-checks.sh`](../../tests/proof/requirement-5/forwarded-headers-checks.sh), with `before` and `after`
phases. Both the API and Nginx logs identify the distinct clients after the fix.
Saved evidence: [before](../../tests/proof/requirement-5/results/forwarded-headers-before-20261123.txt),
[after](../../tests/proof/requirement-5/results/forwarded-headers-after-20261123.txt), and
[verification](../../tests/proof/requirement-5/results/forwarded-headers-verification-20261123.txt).

### 4. Product images are on the local disk

**Where.** [`LocalImageStorage.cs:10`](../../src/Modules/Products/Shop.Modules.Products/Images/LocalImageStorage.cs#L10),
used by uploads and by the demo seed ([`DemoProductSeeder.cs:53`](../../src/Modules/Products/Shop.Modules.Products/Seeding/DemoProductSeeder.cs#L53)).

**What goes wrong.** An image exists only on the instance that saved it.

**What the run showed.** An image uploaded through api1 was found by 3 of 9 requests (api1 only). The demo images
exist only on the instance that won the seeding race (api3 in the saved run); the other two return 404 for them.

**Watch out.** Today's compose mounts a named volume, `images:/data/images`
([`compose.yaml:38`](../../compose.yaml#L38)). If R5-03 mounts it into all three instances, the check passes on one
machine and the problem hides until there are two. Show the "before" without a shared folder, as the script does, and
say why.

**Fix: R5-07**, object storage.

**Before → after.** Check 4: before, 3 of 9; after, 9 of 9, demo images included.

### 5. HybridCache keeps a local copy

**Where.** [`CachedCatalogReader.cs:52`](../../src/Modules/Products/Shop.Modules.Products/Catalog/CachedCatalogReader.cs#L52)
keeps every catalog entry in memory for `CatalogCache:LocalTtl`, 5 seconds
([`appsettings.json:18`](../../src/Shop.Api/appsettings.json#L18)). An edit clears Redis and the local copy of the
instance that handled it ([`CatalogCacheInvalidator.cs:33`](../../src/Modules/Products/Shop.Modules.Products/Catalog/CatalogCacheInvalidator.cs#L33)),
but no other instance hears about it.

**What goes wrong.** The other instances serve the old product, list or categories until their local copy expires.
That is **at most 5 seconds**, not the 10-minute Redis lifetime.

**What the run showed.** After a rename through api1, api2 and api3 kept returning the old name for about 4 to 5
seconds, until their local copies expired. With read routing on (R3-11, off by default), the admin who made the edit skips the caches for 10 seconds and sees
the change everywhere; everyone else doesn't.

**Fix: R5-08**: no local copy for product details (`HybridCacheEntryFlags.DisableLocalCache`), or eviction on a
`product.updated` event every instance receives. Accepting 5 seconds and saying so is also a valid choice.

**Before → after.** Check 3: read every instance once, edit through one, then read all three within 5 seconds.
Before, the others show the old name; after, none do. A check that reads more than 5 seconds after the edit can't
show the problem.

### 6. Each instance may open 100 database connections

**Where.** No connection string sets `Maximum Pool Size`, so Npgsql allows 100 per instance; Postgres's
`max_connections` is 100 (the run read it back). With read routing on, each instance has a second pool for the
replica, and each worker has its own small pool.

**What goes wrong.** Under load, requests queue, each pool grows, and three instances can ask for 300 connections.
Postgres refuses the rest with `53300 too_many_connections`.

**What the run showed.** Only the numbers (check 8). The load runs already showed it with one instance: "one run in
four collapsed as connections reached 99" ([baseline](../../perf/results/baseline/README.md)), and a 1,000
requests/s run "saturated PostgreSQL connections (`too many clients already`)" ([after Redis](../../perf/results/after-redis/README.md)).

**Fix: R5-09.** Size the pool so instances × pool + workers + tools stays below `max_connections`, on the primary and
the replica; for example 25 per API instance.

**Before → after.** `perf/run.sh` at the same rate with one and with three instances: before, connections at the
limit with errors under stress; after, below it with none.

### 7. SignalR negotiates, then connects

**Where.** The live pages connect with `.withUrl(hub, { accessTokenFactory })`
([`session.js:101`](../../src/Shop.Api/wwwroot/app/js/session.js#L101)): first a `negotiate` request, then the
WebSocket, which must reach the same instance.

**What goes wrong.** Behind `least_conn`, the WebSocket often reaches another instance, which doesn't know the
connection and answers 404. The page fails to connect.

**What the run showed.** Negotiate on api1, then the WebSocket on api1: `101 Switching Protocols`; on api2:
`404 Not Found`. A WebSocket without negotiation got 101 on api2. Through Nginx it is worse: R5-02's checks saw 10
of 10 negotiated connections fail, because with nothing else going on `least_conn` takes turns, so the WebSocket
always reaches the next instance.

**Fix: R5-10**, and it is smaller than the plan says. Reconnecting and reloading the page's state already exist
since R4-08 (`withAutomaticReconnect` and [`onreconnected`](../../src/Shop.Api/wwwroot/app/js/session.js#L108)).
Only `skipNegotiation: true` with the WebSockets transport is missing.

**Before → after.** Check 5 without Nginx. Through Nginx: open the order page about ten times; before, some fail to
connect; after, none do. Done in R5-10: in a headless browser through Nginx (the Nginx checks, step 8), the pages'
`connectLive` connected 10 times out of 10 without a negotiate request.

### 8. The OpenAPI `servers` URL comes from the Host header

**Where.** ASP.NET Core's OpenAPI document builds its `servers` entry from the request's scheme and `Host`. The docs
page (Scalar) sends its "try it" requests there.

**What goes wrong.** The plan's Nginx config sends `proxy_set_header Host $host;`, and `$host` has no port. Through
Nginx on `http://localhost:8080`, the docs would say `http://localhost/` and send requests to port 80. Without any
`Host` line, Nginx sends `api`, the upstream's name.

**What the run showed.** `Host: localhost` gives `http://localhost/`; `Host: localhost:8080` gives
`http://localhost:8080/`. The `Location` header stayed relative (`/api/v1/products/<id>`) even with
`Host: api1:8080`, because the code builds it from the path alone
([`CreateOrderEndpoint.cs:124`](../../src/Modules/Orders/Shop.Modules.Orders/Placement/CreateOrderEndpoint.cs#L124)).

**Fix: R5-02.** `proxy_set_header Host $http_host;` in both locations, which keeps the port. R5-05's forwarded
headers add the scheme, which only matters if Nginx ever ends TLS.

**Before → after.** Through Nginx, `curl -s localhost:8080/openapi/v1.json | jq .servers`: before,
`http://localhost/`; after, `http://localhost:8080/`. Done in R5-02: its checks show `http://127.0.0.1:58080/`
through Nginx.

### 9. Warehouse release retries don't claim their rows

**Where.** Every instance polls `pending_releases` every 5 seconds with a plain `SELECT` of the rows that are due
([`PendingReleaseProcessor.cs:14`](../../src/Modules/Orders/Shop.Modules.Orders/Releases/PendingReleaseProcessor.cs#L14)).
Nothing stops two instances from taking the same row.

**What goes wrong.** Instances retry the same release at the same time. Stock stays right: the warehouse locks the
reservation and gives stock back only while it is still `reserved`
([`store.go:129`](https://github.com/AliAsaad715/Inventory/blob/73bef9102ce5572a15691feba1c1f3aad219ef3b/internal/inventory/store.go#L129)
in the warehouse submodule). But each release goes to the warehouse up to
three times. With the warehouse up, the slower instances then delete a row that is already gone
([line 23](../../src/Modules/Orders/Shop.Modules.Orders/Releases/PendingReleaseProcessor.cs#L23)). EF Core reports
that as a `DbUpdateConcurrencyException`, which the worker logs as an error, skipping the rest of that instance's
batch until the next poll. That part is from the code: the run had the warehouse down.

**What the run showed.** Of 20 queued releases, 15 had their first retry run by more than one instance (5 to 19 in
other runs: it depends on when each instance's timer fires).

**Fix: not in the plan**, which lists this job as safe. Claim the rows like the outbox does, with
`FOR UPDATE SKIP LOCKED`, and push `next_attempt_at` forward in the same statement as a short lease, so no
transaction stays open during the warehouse call. It could join R5-11 or become R5-13.

**Before → after.** Check 7: before, rows retried by more than one instance; after, none.

## Safe, and why

| What | Why three instances are fine | Proof |
| --- | --- | --- |
| The JWT signing key | Read from `Jwt__SigningKey`; every instance gets the same `.env` | A token from api3 is accepted by all three (check 9) |
| Account lockout | Identity keeps the failure count and lockout end in `users.users` | Five wrong passwords spread over three instances; the right one is then refused on api3 (check 9) |
| The outbox relay | Claims rows with `FOR UPDATE SKIP LOCKED` ([`OutboxProcessor.cs:35`](../../src/Modules/Orders/Shop.Modules.Orders/Outbox/OutboxProcessor.cs#L35)). The in-process wake-up only speeds up its own instance; every relay also polls each second | `OutboxTests.ASecondRelaySkipsTheRowsTheFirstHasLockedInsteadOfWaitingForThem` |
| Live updates | Each instance declares its own exclusive queue bound to `order.*` ([`ShopTopology.cs:61`](../../src/Shop.Messaging/ShopTopology.cs#L61)), gets every event and pushes to its own connections. SignalR groups and `RecentRealtimeMessages` are local on purpose | `RealtimeDeliveryTests.EachInstanceHasItsOwnExclusiveQueueAndReceivesTheSameMessage` |
| Email duplicates between workers | `processed_messages` is in the shared database: checked before sending, recorded after ([`ProcessedMessages.cs:22`](../../src/Modules/Notifications/Shop.Modules.Notifications/Processing/ProcessedMessages.cs#L22)). A crash between the two can still send twice, as ADR 0001 accepts | The Requirement 4 proof kit, step 3 |
| Data Protection | Nothing uses it: no cookies, no Identity token providers, and antiforgery is off on the one form endpoint ([`CreateProductEndpoint.cs:26`](../../src/Modules/Products/Shop.Modules.Products/Creation/CreateProductEndpoint.cs#L26)). Revisit if cookies or password-reset tokens arrive | No Data Protection line in any log, Information level included (check 9) |
| The read-your-writes marker (R3-11) | Written to Redis, which every instance reads. The extra local copy ([`DatabaseReadRouter.cs:51`](../../src/Shop.SharedKernel/Persistence/DatabaseReadRouter.cs#L51)) only matters if Redis fails during the write: then other instances may read the replica for that user for 10 seconds. Read routing is off by default | From the code |
| Everything else the search found | Static fields are read-only (timeouts, JSON options, and a dummy password hash that only evens out login timing); the `/app` pages ship in the image; logs go to the console; the gRPC retry policy is per call; HTTPS redirection finds no HTTPS port, so it never redirects, and HSTS is only sent over HTTPS | One "Failed to determine the https port for redirect" warning per instance |

## What the plan gets wrong

1. **Absolute URLs.** The `Location` header is already relative with any `Host` (finding 8). Only the OpenAPI
   `servers` entry is absolute.
2. **The Nginx config.** `proxy_set_header Host $host;` drops the port; use `$http_host` (finding 8).
3. **"The `pending_releases` retries claim rows with `FOR UPDATE SKIP LOCKED`."** They don't (finding 9). Only the
   outbox relay does.
4. **"EF Core 9 and later lock the database while migrating."** True, but the lock didn't stop two instances applying
   the same migration (finding 1). The migrator is needed for migrations too, not only for seeding.
5. **The cache row.** The old data lasts at most `CatalogCache:LocalTtl` (5 seconds), not "until theirs expires", and
   its check must read within those 5 seconds (finding 5).
6. **R5-04's scope.** It must also cover the worker's notifications migrations and the demo images (finding 1).
7. **R5-10's scope.** Reconnecting and reloading already exist (finding 7).
8. **The compose sketch** waits for RabbitMQ to be healthy. Today the API deliberately doesn't wait: orders still save
   while RabbitMQ is down, and the outbox holds their events. R5-03 should keep `service_started` for `rabbitmq` and
   `inventory`.

## Notes for later tasks

- **R5-03.** Every API instance connects to RabbitMQ with the same name, `shop-api`
  ([`Program.cs:24`](../../src/Shop.Api/Program.cs#L24)); add `INSTANCE_NAME` so the RabbitMQ UI tells them apart.
  The `api` service has no restart policy, so a crashed instance stays down; a restart policy would also hide
  finding 1, so add it after R5-04. Done in R5-03: the connections are named `shop-api api1` to `api3`, and `api1`
  originally started before the other two. R5-04 replaces that temporary ordering with the shared migrator;
  the API restart policy is unchanged.
- **R5-04.** Implemented, including the notifications schema and shared demo images. Check 1 initializes first;
  its "after" is three instances that start without migrating or seeding.
- **R5-11.** The email worker's prefetch is 5 today
  ([`NotificationsModule.cs:60`](../../src/Modules/Notifications/Shop.Modules.Notifications/NotificationsModule.cs#L60));
  the plan wants 1.
- **R5-12.** The "before" of each problem is a check in the script. Once R5-03 is in, the same checks can also run
  through Nginx, where each becomes a count out of nine requests.
