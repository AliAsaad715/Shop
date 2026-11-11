# ETags on product details (task R3-09)

The proof that `GET /api/v1/products/{id}` answers `304 Not Modified` with no body while a product hasn't changed,
and what that saves. Run on 2026-11-11 at `r3-after-db-4-g8fc3670-dirty`: the R3-09 work, before it was committed,
on top of `main` at `8fc3670`. Rider and Chrome were open, so the latencies here don't compare with the steps' runs.
These runs count 304s and bytes, which don't depend on how busy the laptop was.

## Two curl calls, then an order and an edit

From the seeded snapshot, on the product the load test views most:

```text
1. $ curl -i "$API/api/v1/products/$ID"
HTTP/1.1 200 OK
Cache-Control: no-cache
ETag: "v882-c882-s1000000"
body: name "Classic Watch 3190", price 146.56, available 1000000

2. $ curl -i -H 'If-None-Match: "v882-c882-s1000000"' "$API/api/v1/products/$ID"
HTTP/1.1 304 Not Modified
Cache-Control: no-cache
ETag: "v882-c882-s1000000"

3. customer1 orders one unit: 201
   $ curl -i -H 'If-None-Match: "v882-c882-s1000000"' "$API/api/v1/products/$ID"
HTTP/1.1 200 OK
Cache-Control: no-cache
ETag: "v882-c882-s999999"
body: name "Classic Watch 3190", price 146.56, available 999999

4. an edit: UPDATE products.products SET price = price + 1 WHERE id = '$ID'
   $ curl -i -H 'If-None-Match: "v882-c882-s999999"' "$API/api/v1/products/$ID"
HTTP/1.1 200 OK
Cache-Control: no-cache
ETag: "v175541-c882-s999999"
body: name "Classic Watch 3190", price 147.56, available 999999

5. $ curl -i -H 'If-None-Match: "v175541-c882-s999999"' "$API/api/v1/products/$ID"
HTTP/1.1 304 Not Modified
Cache-Control: no-cache
ETag: "v175541-c882-s999999"
```

The order changed the stock and the edit changed the row's version (`xmin`), so each made a new ETag, and the next
request showed the new data at once. `PUT /products/{id}` comes with R3-08, so the edit here is SQL.

## k6: product details alone, with and without ETags

Both runs start from the seeded snapshot (`perf/snapshot.sh restore`) and run only product details, at the 320/s they
get in the mix at 800 req/s, for a 30 s warm-up and 3 minutes measured. No orders run, so no product changes:

```sh
k6 run -e RATE=800 -e ONLY=product_details -e ETAGS=off -e LABEL=etag-off -e RESULTS_DIR=perf/results/etag perf/load.js
k6 run -e RATE=800 -e ONLY=product_details -e ETAGS=on  -e LABEL=etag-on  -e RESULTS_DIR=perf/results/etag perf/load.js
```

| Run | Requests | 304s | Data received | Bytes a response |
| --- | --- | --- | --- | --- |
| [`etag-off`](etag-off-20261111T163721Z.json) | 57,600 | 0 | 56.3 MB | 978 |
| [`etag-on`](etag-on-20261111T164108Z.json) | 57,601 | 25,906 (45.0%) | 40.2 MB (−28.5%) | 699 |

A 200 cost 978 bytes on the wire and a 304 about 357: the same headers without the body. 45% of the views were a VU
coming back to a product it had already seen. Each VU stands in for one client with its own cache, and nothing
changed between its visits, so every one of those was a 304; the other 55% were first visits.

## The full mix, with orders

One run of the whole mix at 800 req/s with ETags on, as a preview of the "After ETag" step (not saved here: that
step is three `perf/run.sh` runs on a quiet laptop):

| Run | product_details requests | 304s | Bytes a response |
| --- | --- | --- | --- |
| Baseline (before R3-09) | – | – | 924 |
| Full mix, ETags on | 57,600 | 868 (1.5%) | 966 |

Almost no revalidation matched, because of orders. The ETag includes the stock, so every order makes every client's
copy of its products stale, and the products viewed most are the ones ordered most. At 800 req/s, orders change stock
80 times a second and each VU views 2 products a second, both spread over the same popular products, so between two
of a VU's visits to a product its stock changes about 40 times on average, however popular it is. Only about one
revisit in 40 finds the same stock.

Every 200 also carries 54 more bytes than before (the `ETag` and `Cache-Control` headers), and the few 304s win back
12, so in the full mix a product details response costs 4.5% more than in the baseline.

## What it means

- The mechanism works: an unchanged product costs 357 bytes instead of 978, and the client still sees every change at
  once, since it asks every time.
- The database and the warehouse do the same work for a 304 (`DbQueryCountTests`: one query either way). An ETag saves
  the transfer and the serialization; Redis (R3-07) is what saves reads.
- Prediction for the "After ETag" step in the plan's results table: about 1.5% 304s in product details, about 4.5%
  more bytes a response than the baseline, and p50, p95 and p99 the same within noise.
- The details would revalidate well only if an order didn't change their body: for example, with the exact count
  left out (stock fetched on its own, or only in or out of stock shown). That's a product decision, so R3-09 leaves
  the body as it is.
