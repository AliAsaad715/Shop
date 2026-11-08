-- The Requirement 3 dataset (task R3-01): 50,000 customers, 5,000 products in 20 categories, 500,000 orders and about
-- 1.5 million order lines, skewed like a real shop: a few products sell far more than the rest, and a few customers
-- order far more. setseed() makes every run produce the same rows, so every step is measured on the same data.
-- perf/seed.sh runs it once on a freshly migrated database; the warehouse stocks the same product IDs
-- (tests/proof/app-seed.sql).
BEGIN;

-- One random sequence, drawn in one order: no parallel workers, and no scans that start mid-table
SET LOCAL max_parallel_workers_per_gather = 0;
SET LOCAL synchronize_seqscans = off;
SELECT setseed(0.42) \gset

\echo 'Categories and products'

-- The 4 categories every database has, and 16 more; index 0 gets the most products
INSERT INTO products.categories (id, name)
SELECT md5('category-' || n)::uuid, name
FROM unnest(ARRAY['Sports', 'Toys', 'Beauty', 'Garden', 'Kitchen', 'Office', 'Pets', 'Music', 'Movies', 'Games',
                  'Health', 'Automotive', 'Tools', 'Jewelry', 'Shoes', 'Grocery']) WITH ORDINALITY AS c (name, n);

CREATE TEMP TABLE seed_categories ON COMMIT DROP AS
SELECT (row_number() OVER (ORDER BY id::text) - 1)::int AS idx, id
FROM products.categories
WHERE id IN ('019a2180-0000-7000-8000-000000000001', '019a2180-0000-7000-8000-000000000002',
             '019a2180-0000-7000-8000-000000000003', '019a2180-0000-7000-8000-000000000004')
   OR id IN (SELECT md5('category-' || n)::uuid FROM generate_series(1, 16) AS n);

-- Category sizes run from about 680 products down to about 170. Prices lean cheap, and the products were added over
-- the three years before 2026-11-01, a fixed date so every run gets the same timestamps
CREATE TEMP TABLE seed_products ON COMMIT DROP AS
SELECT n,
       md5('product-' || n)::uuid AS id,
       (ARRAY['Classic', 'Compact', 'Deluxe', 'Eco', 'Essential', 'Pro', 'Smart', 'Travel', 'Ultra', 'Vintage'])[1 + n % 10]
         || ' ' || (ARRAY['Backpack', 'Bottle', 'Chair', 'Headphones', 'Jacket', 'Lamp', 'Mug', 'Notebook', 'Speaker',
                          'Watch'])[1 + (n / 10) % 10] || ' ' || n AS name,
       floor(20 * random() ^ 1.5)::int AS category_idx,
       round((2 + 498 * random() ^ 2)::numeric, 2) AS price,
       timestamptz '2026-11-01 00:00:00+00' - random() * interval '1095 days' AS created_at,
       60 + floor(random() * 440)::int AS description_length
FROM generate_series(1, 5000) AS n;

INSERT INTO products.products (id, name, description, price, image_path, created_at, category_id)
SELECT p.id, p.name,
       left(repeat('A durable, well-made product for everyday use, chosen by thousands of customers. ', 8), p.description_length),
       p.price, 'seed/' || p.n || '.jpg', p.created_at, c.id
FROM seed_products p
JOIN seed_categories c ON c.idx = p.category_idx
ORDER BY p.created_at, p.n;

\echo 'Customers'

-- Everyone signs in with Shopper123!. One hash for all, made by the app's own PasswordHasher with its default
-- settings, because hashing 50,000 passwords one by one would take close to an hour
INSERT INTO users.users (id, user_name, normalized_user_name, email, normalized_email, email_confirmed, password_hash,
                         security_stamp, concurrency_stamp, phone_number_confirmed, two_factor_enabled, lockout_enabled,
                         access_failed_count)
SELECT md5('customer-' || n)::uuid,
       'customer' || n || '@shop.test', upper('customer' || n || '@shop.test'),
       'customer' || n || '@shop.test', upper('customer' || n || '@shop.test'),
       false, 'AQAAAAIAAYagAAAAEJ0dSjVcQTDC+ACkK4XIDpROPCy0zuYvZseiknwqw+n42/1hdCWCPn+dXNp+KFissA==',
       upper(md5('stamp-' || n)), md5('concurrency-' || n)::uuid::text,
       false, false, true, 0
FROM generate_series(1, 50000) AS n;

INSERT INTO users.user_roles (user_id, role_id)
SELECT md5('customer-' || n)::uuid, '019a2180-0000-7000-8000-000000000101' -- Customer
FROM generate_series(1, 50000) AS n;

\echo 'Orders'

-- 500,000 orders over the two years before 2026-11-01. A customer's share falls with their number: customer 1 has
-- about 2,200 orders, customer 100 about 110, customer 10,000 about 11 and the last ones about 5. One in twenty
-- is cancelled
CREATE TEMP TABLE seed_orders ON COMMIT DROP AS
SELECT n,
       md5('order-' || n)::uuid AS id,
       md5('customer-' || (1 + floor(50000 * random() ^ 2)::int))::uuid AS customer_id,
       timestamptz '2026-11-01 00:00:00+00' - random() * interval '730 days' AS created_at,
       1 + floor(random() * 5)::int AS line_count,
       random() < 0.05 AS cancelled
FROM generate_series(1, 500000) AS n;

-- One to five lines an order, three on average. Product n's share falls with n: product 1 is on about 21,000 lines,
-- product 100 on about 1,000 and product 5,000 on about 150. Most lines are for one unit
CREATE TEMP TABLE seed_picks ON COMMIT DROP AS
SELECT o.n AS order_n, j,
       1 + floor(5000 * random() ^ 2)::int AS product_n,
       1 + floor(3 * random() ^ 4)::int AS quantity
FROM seed_orders o
CROSS JOIN LATERAL generate_series(1, o.line_count) AS j;

-- An order holds a product once, so a product drawn twice for the same order keeps its first line
CREATE TEMP TABLE seed_lines ON COMMIT DROP AS
SELECT DISTINCT ON (order_n, product_n) order_n, j, product_n, quantity
FROM seed_picks
ORDER BY order_n, product_n, j;

-- Where each order got to, by its age: an hour to be confirmed, two days to ship and six to arrive; a cancelled
-- order was cancelled two hours after it was placed. The status, the last change and the version follow the
-- app's own transitions
CREATE TEMP TABLE seed_states ON COMMIT DROP AS
SELECT o.n, o.id, o.customer_id, o.created_at, t.total, s.status,
       o.created_at + CASE s.status WHEN 'Pending' THEN interval '0' WHEN 'Confirmed' THEN interval '1 hour'
                                    WHEN 'Cancelled' THEN interval '2 hours' WHEN 'Shipped' THEN interval '2 days'
                                    ELSE interval '6 days' END AS updated_at,
       CASE s.status WHEN 'Pending' THEN 0 WHEN 'Confirmed' THEN 1 WHEN 'Cancelled' THEN 1 WHEN 'Shipped' THEN 2
                     ELSE 3 END AS version
FROM seed_orders o
JOIN (SELECT l.order_n, sum(p.price * l.quantity) AS total
      FROM seed_lines l JOIN seed_products p ON p.n = l.product_n
      GROUP BY l.order_n) t ON t.order_n = o.n
CROSS JOIN LATERAL (
  SELECT CASE WHEN o.cancelled AND timestamptz '2026-11-01 00:00:00+00' - o.created_at >= interval '2 hours' THEN 'Cancelled'
              WHEN timestamptz '2026-11-01 00:00:00+00' - o.created_at < interval '1 hour' THEN 'Pending'
              WHEN timestamptz '2026-11-01 00:00:00+00' - o.created_at < interval '2 days' THEN 'Confirmed'
              WHEN timestamptz '2026-11-01 00:00:00+00' - o.created_at < interval '7 days' THEN 'Shipped'
              ELSE 'Delivered' END AS status) s;

-- Rows go in in the order the shop took them, oldest first, as a real database fills up
INSERT INTO orders.orders (id, customer_id, status, total, created_at, updated_at, version)
SELECT id, customer_id, status, total, created_at, updated_at, version
FROM seed_states
ORDER BY created_at, n;

\echo 'Order lines'

-- Name and price are copied from the product, as the app does when it takes an order
INSERT INTO orders.order_lines (id, order_id, product_id, product_name, unit_price, quantity, line_total)
SELECT md5('line-' || l.order_n || '-' || l.j)::uuid, o.id, p.id, p.name, p.price, l.quantity, p.price * l.quantity
FROM seed_lines l
JOIN seed_orders o ON o.n = l.order_n
JOIN seed_products p ON p.n = l.product_n
ORDER BY o.created_at, o.n, l.j;

\echo 'Status history'

-- One row per status each order has been in: placed by its customer, moved on by the admin
INSERT INTO orders.order_status_history (id, order_id, from_status, to_status, changed_by, changed_at)
SELECT md5('history-' || o.n || '-' || step.k)::uuid, o.id, step.from_status, step.to_status,
       CASE WHEN step.k = 0 THEN o.customer_id ELSE coalesce(admin.id, o.customer_id) END,
       o.created_at + step.elapsed
FROM seed_states o
LEFT JOIN (SELECT ur.user_id AS id
           FROM users.user_roles ur
           WHERE ur.role_id = '019a2180-0000-7000-8000-000000000102' -- Admin
           ORDER BY ur.user_id
           LIMIT 1) admin ON true
CROSS JOIN (VALUES (0, NULL, 'Pending', interval '0'),
                   (1, 'Pending', 'Confirmed', interval '1 hour'),
                   (2, 'Confirmed', 'Shipped', interval '2 days'),
                   (3, 'Shipped', 'Delivered', interval '6 days'),
                   (4, 'Pending', 'Cancelled', interval '2 hours')) AS step (k, from_status, to_status, elapsed)
WHERE step.k = 0
   OR (step.k BETWEEN 1 AND 3 AND o.status <> 'Cancelled' AND o.version >= step.k)
   OR (step.k = 4 AND o.status = 'Cancelled')
ORDER BY o.created_at, o.n, step.k;

COMMIT;

-- Fresh statistics and a visibility map, so the first query plans are the ones every later run sees
\echo 'Vacuum and analyze'
VACUUM (ANALYZE) products.categories, products.products, users.users, users.user_roles,
                 orders.orders, orders.order_lines, orders.order_status_history;
