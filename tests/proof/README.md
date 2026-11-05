# Requirement 2 proof (task R2-10)

Six steps show the last-unit race, the warehouse down and slow, recovery, and the tests. Run them from the
repository root on a fresh stack. Product X is `019a2180-0000-7000-8000-000000000205`; the warehouse seed
stocks it with exactly 1.

Shell helpers for the steps below (a customer, a token and one order for X):

```sh
API=http://localhost:8080
X=019a2180-0000-7000-8000-000000000205
CUSTOMER='{"email":"demo@shop.test","password":"Demo123!x"}'
curl -s -o /dev/null -H 'Content-Type: application/json' -d "$CUSTOMER" $API/api/v1/auth/register
TOKEN=$(curl -s -H 'Content-Type: application/json' -d "$CUSTOMER" $API/api/v1/auth/login | grep -o '"accessToken":"[^"]*"' | cut -d'"' -f4)
order() { curl -s -i -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d "{\"lines\":[{\"productId\":\"$X\",\"quantity\":1}]}" $API/api/v1/orders; echo; }
stock() { curl -s $API/api/v1/products/$X | grep -o '"stockStatus":"[^"]*","available":[^,}]*'; }
```

## 1. Start everything

```sh
git clone --recurse-submodules https://github.com/AliAsaad715/Shop.git && cd Shop
cp .env.example .env    # then set JWT_SIGNING_KEY
docker compose up --build --wait
stock                   # "stockStatus":"in_stock","available":1
```

## 2. The last unit, twenty times at once

[`order-race.js`](order-race.js) logs one customer in, then sends 20 orders for X at the same instant.

```sh
k6 run tests/proof/order-race.js
# or without installing k6:
docker run --rm -i --network host -v "$PWD/tests/proof:/proof" grafana/k6:2.3.0 run /proof/order-race.js
```

Expect `orders_created` 1, `orders_refused` 19, every threshold passing, and X sold out afterwards. The race
leaves X at 0, so reseed the warehouse before running it again:

```sh
docker compose rm -sf inventory-db && docker volume rm shop_inventory-db-data && docker compose up -d --wait
```

## 3. Warehouse stopped

```sh
docker compose stop inventory
order                   # 503, code orders.warehouse_unavailable, Retry-After: 5, in well under 2 s
stock                   # "stockStatus":"unknown","available":null, and the page still answers 200
curl -s $API/health     # Degraded
```

## 4. Warehouse slow

`INVENTORY_DELAY_MS` holds every warehouse call before it is handled; 3000 is past the app's 2 s deadline.

```sh
INVENTORY_DELAY_MS=3000 docker compose up -d --wait inventory
order                   # 504, code orders.warehouse_timeout
stock                   # "unknown" after the 500 ms stock deadline
curl -s $API/health     # Healthy: health checks never wait
```

The delayed `Reserve` never reached the warehouse's tables, so the stock is unchanged. The app's release of
that order timed out too, so it waits in `orders.pending_releases` until the warehouse is back.

## 5. Back to normal

```sh
docker compose up -d --wait inventory    # INVENTORY_DELAY_MS back to 0
order                                    # 201 again, if X has stock left
docker compose logs api inventory | grep <order id>
```

Both services log each call with the same order ID: the app as `Reserved stock for order <id>`, the warehouse
as a JSON line with `"order_id"`. The worker also finishes the release queued in step 4 on its next retry;
retries back off from 5 s up to 5 minutes.

## 6. Tests

```sh
dotnet test Shop.slnx -m:1
(cd warehouse && go test -race ./...)
```

The .NET side includes `InventoryFailureTests.FailedSaveReleasesTheReservationAndIs500`, which fails the
order's save after a successful reserve and proves the reservation is released and no order is kept. The Go
side includes `TestStorePostgres`, whose 20 simultaneous orders for the last unit end with 1 accepted and 19
rejected; it starts PostgreSQL with Testcontainers, so Docker must be running.
