INSERT INTO stock_levels (product_id, available)
SELECT md5('product-' || n)::uuid, 1000000 FROM generate_series(1, 5000) AS n
ON CONFLICT (product_id) DO UPDATE SET available = EXCLUDED.available;