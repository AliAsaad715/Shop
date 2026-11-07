// Twenty orders for the last unit of product X, all sent at the same instant by one customer:
// the warehouse must accept exactly one and refuse the other nineteen, leaving X sold out.
// Run it against a fresh `docker compose up` (the seed stocks X with exactly 1):
//
//   k6 run tests/proof/order-race.js
//   docker run --rm -i --network host -v "$PWD/tests/proof:/proof" grafana/k6:2.3.0 run /proof/order-race.js
//
// API_URL and PRODUCT_ID override the defaults: k6 run -e API_URL=http://localhost:18080 ...
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';

const API = __ENV.API_URL || 'http://localhost:8080';
const PRODUCT_X = __ENV.PRODUCT_ID || '019a2180-0000-7000-8000-000000000206';
const ORDERS = 20;
const JSON_HEADERS = { 'Content-Type': 'application/json' };

const created = new Counter('orders_created');
const refused = new Counter('orders_refused');

export const options = {
  scenarios: {
    race: { executor: 'per-vu-iterations', vus: ORDERS, iterations: 1, maxDuration: '1m' },
  },
  thresholds: {
    orders_created: ['count==1'],
    orders_refused: [`count==${ORDERS - 1}`],
    checks: ['rate==1'],
  },
};

export function setup() {
  // One customer places every order, so a single login stays under the login rate limit
  const credentials = JSON.stringify({ email: `race-${Date.now()}@shop.test`, password: 'Race123!x' });
  const registered = http.post(`${API}/api/v1/auth/register`, credentials, { headers: JSON_HEADERS });
  check(registered, { 'customer registered': (r) => r.status === 201 });
  const login = http.post(`${API}/api/v1/auth/login`, credentials, { headers: JSON_HEADERS });
  check(login, { 'customer logged in': (r) => r.status === 200 });

  const before = http.get(`${API}/api/v1/products/${PRODUCT_X}`).json();
  check(before, { 'product X starts with 1 in stock': (p) => p.available === 1 });

  // Every order leaves at the same instant, two seconds from now
  return { token: login.json('accessToken'), startAt: Date.now() + 2000 };
}

export default function (data) {
  sleep(Math.max(0, data.startAt - Date.now()) / 1000);

  const headers = { 'Content-Type': 'application/json', Authorization: `Bearer ${data.token}` };
  const res = http.post(`${API}/api/v1/orders`,
    JSON.stringify({ lines: [{ productId: PRODUCT_X, quantity: 1 }] }), { headers });

  // Zeros count too, so a run with no 201 at all still fails its threshold instead of skipping it
  created.add(res.status === 201 ? 1 : 0);
  refused.add(res.status === 409 ? 1 : 0);
  check(res, { 'order is 201 or 409': (r) => r.status === 201 || r.status === 409 });
}

export function teardown() {
  const after = http.get(`${API}/api/v1/products/${PRODUCT_X}`).json();
  console.log(`product X after the race: ${after.stockStatus}, available ${after.available}`);
  check(after, { 'product X is sold out': (p) => p.available === 0 });
}
