# Query plans (task R3-05)

Keyset pagination made the deepest catalog page 41 times cheaper in the database, 4.09 ms down to 0.10 ms, and cut
the database's total statement time by 19% under the same load. The plan's suggested indexes already existed, so
the change was their direction and the shape of the query.

## How the queries were chosen

`pg_stat_statements` (R3-04) ranked the app's statements by total time during a k6 run of the R3-02 mix at
400 req/s ([`top-queries.before.json`](top-queries.before.json)). That's a single run on a laptop running on battery,
used to rank the queries, not a measured step. The top five, with their share of the database's time:

| Share | Query |
| --- | --- |
| 24.1% (+ 5.5% + 3.7% for page 2 and all products) | A catalog page: `ORDER BY created_at DESC, id LIMIT 20 OFFSET n` |
| 12.5% | The category's `COUNT(*)`, for `totalCount` |
| 12.4% | The `COUNT(*)` of all products, 0.84 ms each |
| 9.7% | The `COUNT(*)` of a customer's orders, for "my orders" |
| 6.1% | The "my orders" page, with a `sum(quantity)` subquery per order |

[`perf/explain.sh`](../explain.sh) ran each with real parameters through `EXPLAIN (ANALYZE, BUFFERS)`, keeping the
second of two runs so a cold cache doesn't decide the plan: Electronics, the largest category (675 products), the
last page of all products (offset 4,980), and `customer1`, who has 2,276 orders. Each `.before.txt` and `.after.txt`
here starts with the query it explains.

## What the plans showed, and what changed

| Query | Before | After |
| --- | --- | --- |
| [Last page of all products](all-products-page-last.before.txt) | **4.09 ms**, 246 buffers: a `Seq Scan` of all 5,006 products and a 2.9 MB sort, to keep the last 20 | [**0.10 ms**](all-products-page-last.after.txt), 5 buffers: an `Index Scan` that starts at the cursor and stops after 21 rows |
| [Last page of Electronics](category-page-last.before.txt) | **0.71 ms**, 244 buffers: all 675 of the category's rows fetched and sorted, 660 thrown away | [**0.12 ms**](category-page-last.after.txt), 8 buffers: the index condition seeks straight to the page's 15 rows |
| [First page of Electronics](category-page-first.before.txt) | 0.11 ms: an index scan that stops after 20 rows | [the same plan](category-page-first.after.txt); its 0.20 ms is one noisy sample, and five more of each ran 0.11 to 0.13 ms |
| [Category total](category-count.before.txt), [all products total](all-products-count.before.txt) | 0.31 ms and 1.10 ms: index-only scans over 675 and 5,006 entries, on every list request | Not run at all for a page reached by cursor; page 1 still counts |
| [My orders total](my-orders-count.before.txt) | 0.39 ms: an index-only scan over `customer1`'s 2,276 orders | Unchanged, see below |
| [My orders page](my-orders-page.before.txt) | 0.11 ms: the index stops after 20 orders, and each order's lines are one small index lookup | Unchanged, see below |

A deep `OFFSET` reads every row before the page and throws it away, and on deep enough pages Postgres stops walking
the index at all: fetching and sorting everything is cheaper than fetching almost everything in order. Keyset
pagination asks for the rows after the last one seen instead. That's one row comparison, which an index can answer
directly when its columns run in the same direction as the comparison:

```sql
-- the page after the cursor's product: the index starts there, at any depth
WHERE category_id = $1 AND (created_at, id) < ($2, $3)
ORDER BY created_at DESC, id DESC
LIMIT 21   -- one more than the page, to know whether another page follows
```

The changes in the code:

- **Index direction.** The catalog's indexes were `(category_id, created_at DESC, id)` and `(created_at DESC, id)`.
  The ID, breaking ties, ran the other way, so "after this product" couldn't be one index range. The migration
  `ProductKeysetIndexes` makes both `id DESC`, the plan's own candidate, and the catalog orders ties the same way.
  Only products created at the very same instant change order.
- **Cursor paging.** `GET /api/v1/products` takes `cursor`, an opaque `nextCursor` from an earlier page, and returns
  the page after it, with `page` and `totalCount` null since it skips the count. Numbered pages work as before, total
  included, and also return `nextCursor`. A client shows page 1 with its total, then follows cursors.
- **The load test** reaches page 2 onwards through `nextCursor`, as such a client would, wherever the API offers it.

## What didn't change, and why

- **Page 1 still counts.** A first page shows its total, and the count is already an index-only scan: 0.09 ms for
  an average category. R3-07 caches first pages, which removes it for most requests.
- **"My orders" keeps `OFFSET` and its count.** The mix only asks for pages 1 and 2, where `OFFSET` costs nothing,
  and the count is already the best plan a count has: an index-only scan of one customer's entries. Only not counting
  is faster, with cursor paging for orders too, or a per-customer counter kept up to date on every order.
- **The per-order `sum(quantity)`** is 20 small index lookups, 0.11 ms with the page. Making the index cover
  `quantity` would save a few heap reads, not worth a bigger index on 1.5 million lines.
- **`DISCARD ALL`**, 7.2% of the time and the most frequent statement, is Npgsql resetting each connection it takes
  from the pool. `No Reset On Close=true` in the connection string removes it; that's a change of its own, with its
  own measurement.

## Under load

The same k6 run at 400 req/s, before and after ([`top-queries.after.json`](top-queries.after.json)), one run each on
battery:

| | Before | After |
| --- | --- | --- |
| Statement time in the database | 7,536 ms | 6,116 ms (−19%) |
| Category `COUNT(*)` calls | 10,355 | 6,221 |
| `product_list` p50 / p95 / p99 | 2.8 / 5.2 / 9.4 ms | 2.5 / 4.3 / 6.1 ms |
| All requests p50 / p95 / p99 | 2.7 / 7.4 / 11.7 ms | 2.5 / 6.7 / 9.2 ms |

The direction is clear, but these runs are no measured step: the step's three runs at 1,000 req/s, on mains power,
are what `perf/compare.sh` compares with the baseline (see `perf/README.md`).
