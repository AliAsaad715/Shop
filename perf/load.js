// The Requirement 3 load test (task R3-02). It runs an open model: requests start at a fixed rate whether or not
// earlier ones have answered, so a slow system can't hide its slowness by receiving fewer requests. The traffic
// is mixed like real use, every request is tagged with its endpoint, and each run saves p50, p95 and p99 per
// endpoint to perf/results/. Run it from the repository root; perf/README.md has the settings:
//
//   k6 run -e LABEL=baseline -e COMMIT=$(git describe --tags --always --dirty) perf/load.js
//   docker run --rm -i --network host -u "$(id -u):$(id -g)" -v "$PWD/perf:/shop/perf" -w /shop grafana/k6:2.3.0 \
//     run -e LABEL=baseline -e COMMIT=$(git describe --tags --always --dirty) perf/load.js
import http from 'k6/http';
import exec from 'k6/execution';
import encoding from 'k6/encoding';
import { fail, sleep } from 'k6';

const API = (__ENV.API_URL || 'http://localhost:8080').replace(/\/+$/, '');
const RATE = positive('RATE', 200);            // requests per second, all endpoints together
const WARMUP = __ENV.WARMUP || '30s';          // the same traffic first, left out of the results
const DURATION = __ENV.DURATION || '3m';       // the measured part, after the warm-up
const LABEL = (__ENV.LABEL || 'run').replace(/[^\w.-]+/g, '-');
const COMMIT = __ENV.COMMIT || 'unknown';
const RESULTS_DIR = __ENV.RESULTS_DIR || 'perf/results';
const CUSTOMERS = positive('CUSTOMERS', 5);    // registered only when CUSTOMER_EMAILS is empty
const PAGE_SIZE = 20;                          // what a shopper's list page asks for
const DISCOVERY_PAGE_SIZE = 50;                // the largest page, to read the catalog in setup quickly

const warmupMs = millis('WARMUP', WARMUP);
const durationMs = millis('DURATION', DURATION);
if (durationMs === 0) {
  throw new Error('DURATION must be longer than 0');
}

// The traffic mix, as shares of RATE: mostly browsing, some "my orders", a little buying. Keep it fixed across
// runs; a different mix measures a different system
const ENDPOINTS = {
  product_list: { exec: 'productList', share: 0.40, method: 'GET', route: '/api/v1/products', expect: 200 },
  product_details: { exec: 'productDetails', share: 0.40, method: 'GET', route: '/api/v1/products/{id}', expect: 200 },
  categories: { exec: 'categories', share: 0.05, method: 'GET', route: '/api/v1/categories', expect: 200 },
  my_orders: { exec: 'myOrders', share: 0.10, method: 'GET', route: '/api/v1/orders', expect: 200 },
  create_order: { exec: 'createOrder', share: 0.05, method: 'POST', route: '/api/v1/orders', expect: 201 },
};

// A copy of our own: k6 rewrites the exported options, and the summary reads the VU counts back from here
const SCENARIOS = Object.fromEntries(Object.entries(ENDPOINTS).map(([name, endpoint]) => [name, arrivals(endpoint)]));

export const options = {
  scenarios: SCENARIOS,
  thresholds: thresholds(),
  summaryTrendStats: ['count', 'p(50)', 'p(95)', 'p(99)', 'max'],
  // No url tag: every product ID would make its own time series; the name tag carries the route instead
  systemTags: ['status', 'method', 'name', 'group', 'check', 'error', 'error_code', 'scenario', 'expected_response'],
  // The load requests only need status codes; setup asks for the bodies it reads
  discardResponseBodies: true,
  // Room for setup to wait out the login rate limit when many customers are listed
  setupTimeout: '10m',
};

// One scenario per endpoint, each starting its own share of RATE at fixed intervals
function arrivals(endpoint) {
  const perSecond = RATE * endpoint.share;
  // Per minute, so a share that isn't a whole number per second (5% of 150) still arrives at its exact rate
  const rate = Math.max(1, Math.round(perSecond * 60));
  const measured = { target: rate, duration: `${durationMs}ms` };
  return {
    // The warm-up climbs from a tenth of the rate to all of it, so an app that just started isn't hit at full rate
    // while it's cold, which made k6 run out of VUs and drop requests; the measured part holds the full rate
    executor: 'ramping-arrival-rate',
    exec: endpoint.exec,
    startRate: warmupMs > 0 ? Math.max(1, Math.round(rate / 10)) : rate,
    timeUnit: '1m',
    stages: warmupMs > 0 ? [{ target: rate, duration: `${warmupMs}ms` }, measured] : [measured],
    // Enough VUs up front for responses averaging half a second, and a cap at three seconds: past it k6 drops
    // iterations, and the dropped_iterations threshold fails the run
    preAllocatedVUs: Math.ceil(perSecond / 2) + 2,
    maxVUs: Math.ceil(perSecond * 3) + 10,
  };
}

// k6 reports a tag's own percentiles only for metrics named here; an empty list adds no pass or fail. The two
// real thresholds decide whether a run counts: under 1% errors, and no dropped iterations (k6 ran out of VUs, so
// the rate wasn't held)
function thresholds() {
  const all = {
    'http_req_duration{traffic:measured}': [],
    'http_req_failed{traffic:measured}': ['rate<0.01'],
    'data_received{traffic:measured}': [],
    dropped_iterations: ['count==0'],
  };
  for (const name of Object.keys(ENDPOINTS)) {
    all[`http_req_duration{traffic:measured,endpoint:${name}}`] = [];
    all[`http_req_failed{traffic:measured,endpoint:${name}}`] = [];
    all[`data_received{traffic:measured,endpoint:${name}}`] = [];
  }
  return all;
}

export function setup() {
  const startedAt = new Date().toISOString();
  const catalog = readCatalog();
  const customers = signInCustomers(startedAt);
  // The scenarios start as soon as setup returns; perf/run.sh reads the measured window from this
  return { startedAt, loadStartsAt: Date.now(), ...catalog, ...customers };
}

// Every category and product, read through the API, so the script runs on whatever the seed made. Sorted by
// ID, so the same products are the popular ones in every run
function readCatalog() {
  const categoryIds = getJson('/api/v1/categories').map((category) => category.id).sort();
  if (categoryIds.length === 0) {
    fail(`${API} has no categories`);
  }

  const products = [];
  const lists = [];
  for (const category of categoryIds) {
    let total = 0;
    for (let page = 1; ; page++) {
      const body = getJson(`/api/v1/products?category=${category}&page=${page}&pageSize=${DISCOVERY_PAGE_SIZE}`);
      total = body.totalCount;
      body.items.forEach((item) => products.push(item.id));
      if (body.items.length === 0 || page * DISCOVERY_PAGE_SIZE >= total) {
        break;
      }
    }

    lists.push({ category, pages: pagesOf(total) });
  }

  if (products.length === 0) {
    fail(`${API} has no products; seed the database first`);
  }

  products.sort();
  return { lists, allPages: pagesOf(products.length), products };
}

// Customers for "my orders" and new orders. Seeded customers (CUSTOMER_EMAILS) bring real order histories;
// without them the script registers fresh ones whose order lists start empty, which only suits a trial run
function signInCustomers(startedAt) {
  const listed = (__ENV.CUSTOMER_EMAILS || '').split(',').map((email) => email.trim()).filter(Boolean);
  let emails = listed;
  let password = __ENV.CUSTOMER_PASSWORD;
  if (listed.length > 0 && !password) {
    fail('CUSTOMER_EMAILS needs CUSTOMER_PASSWORD too');
  }

  if (listed.length === 0) {
    password = 'Perf123!x';
    const stamp = startedAt.replace(/\D/g, '');
    emails = Array.from({ length: CUSTOMERS }, (_, n) => `perf-${stamp}-${n + 1}@shop.test`);
    emails.forEach((email) => register(email, password));
  }

  const tokens = emails.map((email) => login(email, password));

  // A token that expires mid-run would turn the rest of the run into 401s
  const runEndsAt = Date.now() + warmupMs + durationMs;
  if (Math.min(...tokens.map(expiresAt)) < runEndsAt + 60 * 1000) {
    fail('the customers\' tokens (Jwt:LifetimeMinutes, 15 by default) expire before the run ends; '
      + 'shorten WARMUP + DURATION, or raise LOGIN_RATE_LIMIT_PERMIT_LIMIT so setup waits less');
  }

  return { customerSource: listed.length > 0 ? 'seeded' : 'registered', tokens };
}

function register(email, password) {
  const res = http.post(`${API}/api/v1/auth/register`, JSON.stringify({ email, password }),
    { headers: { 'Content-Type': 'application/json' }, tags: { traffic: 'setup' } });
  if (res.status !== 201) {
    fail(`registering ${email} answered ${res.status}`);
  }
}

// The API allows 5 logins a minute from one address (LoginRateLimit), so past that, setup waits out the window
function login(email, password) {
  for (let attempt = 1; attempt <= 5; attempt++) {
    const res = http.post(`${API}/api/v1/auth/login`, JSON.stringify({ email, password }),
      { headers: { 'Content-Type': 'application/json' }, responseType: 'text', tags: { traffic: 'setup' } });
    if (res.status === 200) {
      return res.json('accessToken');
    }

    if (res.status !== 429) {
      fail(`logging in as ${email} answered ${res.status}`);
    }

    const wait = Number(res.headers['Retry-After']) || 60;
    console.log(`Login rate limit reached; waiting ${wait} s (LOGIN_RATE_LIMIT_PERMIT_LIMIT in .env raises it)`);
    sleep(wait);
  }

  return fail(`logging in as ${email} kept answering 429`);
}

function expiresAt(token) {
  return JSON.parse(encoding.b64decode(token.split('.')[1], 'rawurl', 's')).exp * 1000;
}

function getJson(path) {
  const res = http.get(`${API}${path}`, { responseType: 'text', tags: { traffic: 'setup' } });
  if (res.status !== 200) {
    fail(`GET ${path} answered ${res.status}; is the API at ${API} up?`);
  }

  return res.json();
}

// One list request in ten shows all products; the rest browse a category, the popular ones more often
export function productList(data) {
  const list = Math.random() < 0.1 ? { category: null, pages: data.allPages } : data.lists[zipf(data.lists.length)];
  const category = list.category ? `category=${list.category}&` : '';
  send('product_list', `/api/v1/products?${category}page=${pickPage(list.pages)}&pageSize=${PAGE_SIZE}`);
}

export function productDetails(data) {
  send('product_details', `/api/v1/products/${data.products[zipf(data.products.length)]}`);
}

export function categories() {
  send('categories', '/api/v1/categories');
}

export function myOrders(data) {
  const page = Math.random() < 0.9 ? 1 : 2;
  send('my_orders', `/api/v1/orders?page=${page}&pageSize=${PAGE_SIZE}`, { token: anyOf(data.tokens) });
}

// One to three popular products, one unit each; the seed stocks the warehouse so these never run out
export function createOrder(data) {
  const wanted = Math.min(1 + Math.floor(Math.random() * 3), data.products.length);
  const productIds = new Set();
  while (productIds.size < wanted) {
    productIds.add(data.products[zipf(data.products.length)]);
  }

  const lines = Array.from(productIds, (productId) => ({ productId, quantity: 1 }));
  send('create_order', '/api/v1/orders', { token: anyOf(data.tokens), body: JSON.stringify({ lines }) });
}

let warned = false; // each VU logs its first unexpected answer; the rest only count as errors

function send(endpoint, path, { token, body } = {}) {
  const { method, route, expect } = ENDPOINTS[endpoint];
  const headers = {};
  if (body) {
    headers['Content-Type'] = 'application/json';
  }

  if (token) {
    headers.Authorization = `Bearer ${token}`;
  }

  // VU tags rather than request tags, because k6 counts data_received per VU, not per request. Requests in the
  // warm-up are tagged apart, and every reported number leaves them out
  exec.vu.tags.endpoint = endpoint;
  exec.vu.tags.traffic = Date.now() - exec.scenario.startTime < warmupMs ? 'warmup' : 'measured';
  const res = http.request(method, `${API}${path}`, body || null,
    { headers, timeout: '10s', tags: { name: `${method} ${route}` } });
  if (res.status !== expect && !warned) {
    warned = true;
    console.warn(`${endpoint}: expected ${expect}, got ${res.status} ${res.error || ''}`.trim());
  }
}

// Most browsing stops at the first pages; one list request in ten goes deeper, anywhere up to the last page
function pickPage(pages) {
  const x = Math.random();
  const page = x < 0.6 ? 1 : x < 0.8 ? 2 : x < 0.9 ? 3 : 4 + Math.floor(Math.random() * Math.max(1, pages - 3));
  return Math.min(page, pages);
}

// Zipf popularity: the item at rank r is picked in proportion to 1/r, so a few products and categories take most
// of the traffic, as in a real shop. Each VU builds the cumulative weights once per list length
const zipfTables = {};

function zipf(n) {
  let table = zipfTables[n];
  if (!table) {
    table = new Float64Array(n);
    let total = 0;
    for (let rank = 1; rank <= n; rank++) {
      total += 1 / rank;
      table[rank - 1] = total;
    }

    zipfTables[n] = table;
  }

  const target = Math.random() * table[n - 1];
  let low = 0;
  let high = n - 1;
  while (low < high) {
    const middle = (low + high) >> 1;
    if (table[middle] < target) {
      low = middle + 1;
    } else {
      high = middle;
    }
  }

  return low;
}

function anyOf(items) {
  return items[Math.floor(Math.random() * items.length)];
}

function pagesOf(count) {
  return Math.max(1, Math.ceil(count / PAGE_SIZE));
}

export function handleSummary(data) {
  const setup = data.setup_data;
  if (!setup) {
    return { stdout: 'No results saved: setup did not finish.\n' };
  }

  const seconds = durationMs / 1000;
  const endpoints = {};
  for (const [name, endpoint] of Object.entries(ENDPOINTS)) {
    endpoints[name] = {
      route: `${endpoint.method} ${endpoint.route}`,
      share: endpoint.share,
      ...stats(data, `{traffic:measured,endpoint:${name}}`, seconds),
    };
  }

  const problems = [];
  for (const [metric, { thresholds: checks }] of Object.entries(data.metrics)) {
    for (const [threshold, { ok }] of Object.entries(checks || {})) {
      if (!ok) {
        problems.push(`${metric} ${threshold}`);
      }
    }
  }

  // A run stopped early (Ctrl+C, an abort) can still pass its thresholds, so check it ran its full length
  const overall = stats(data, '{traffic:measured}', seconds);
  const expected = sum(Object.values(SCENARIOS).map((scenario) => scenario.stages[scenario.stages.length - 1].target / 60))
    * seconds;
  if (overall.requests < expected * 0.95) {
    problems.push(`only ${overall.requests} of about ${Math.round(expected)} requests were measured: the run was cut short`);
  }

  const measuredFrom = setup.loadStartsAt + warmupMs;
  const result = {
    label: LABEL,
    commit: COMMIT,
    startedAt: setup.startedAt,
    api: API,
    settings: { rate: RATE, warmup: WARMUP, duration: DURATION, pageSize: PAGE_SIZE },
    dataset: {
      categories: setup.lists.length,
      products: setup.products.length,
      customers: setup.tokens.length,
      customerSource: setup.customerSource,
    },
    // When the measured part ran, so what the system did can be matched to it
    window: {
      measuredFrom: new Date(measuredFrom).toISOString(),
      measuredTo: new Date(measuredFrom + durationMs).toISOString(),
    },
    overall,
    endpoints,
    droppedIterations: metricValue(data, 'dropped_iterations', 'count'),
    // More VUs than preallocated means responses slowed enough that k6 had to add some; at the cap it drops
    vus: {
      allocated: metricValue(data, 'vus_max', 'max'),
      preallocated: sum(Object.values(SCENARIOS).map((scenario) => scenario.preAllocatedVUs)),
      cap: sum(Object.values(SCENARIOS).map((scenario) => scenario.maxVUs)),
    },
    valid: problems.length === 0,
    problems,
  };

  const file = `${RESULTS_DIR}/${LABEL}-${setup.startedAt.replace(/[-:]|\.\d+/g, '')}.json`;
  return { stdout: report(result, file), [file]: `${JSON.stringify(result, null, 2)}\n` };
}

function stats(data, tags, seconds) {
  const duration = data.metrics[`http_req_duration${tags}`];
  const failed = data.metrics[`http_req_failed${tags}`];
  const received = data.metrics[`data_received${tags}`];
  const values = duration ? duration.values : {};
  const requests = values.count || 0;
  return {
    requests,
    rps: round(requests / seconds, 1),
    p50Ms: round(values['p(50)'], 1),
    p95Ms: round(values['p(95)'], 1),
    p99Ms: round(values['p(99)'], 1),
    maxMs: round(values.max, 1),
    errorRate: failed ? round(failed.values.rate, 4) : 0,
    bytesReceived: received ? received.values.count : 0,
  };
}

function metricValue(data, metric, stat) {
  return data.metrics[metric] ? data.metrics[metric].values[stat] : 0;
}

function sum(numbers) {
  return numbers.reduce((total, n) => total + n, 0);
}

function round(value, digits) {
  return typeof value === 'number' && Number.isFinite(value) ? Number(value.toFixed(digits)) : null;
}

// The console table: the same numbers as the saved file, one line per endpoint
function report(result, file) {
  const { settings, dataset, overall } = result;
  const row = (name, route, s) => [
    name.padEnd(17), route.padEnd(28), fixed(s.rps).padStart(7), fixed(s.p50Ms).padStart(9),
    fixed(s.p95Ms).padStart(9), fixed(s.p99Ms).padStart(9), fixed(s.maxMs).padStart(9),
    `${(s.errorRate * 100).toFixed(2)}%`.padStart(8),
  ].join(' ');

  const warmup = warmupMs > 0 ? `after a ${settings.warmup} warm-up` : 'with no warm-up';
  const lines = [
    '',
    `Load test "${result.label}" at ${result.commit}: ${settings.rate} req/s for ${settings.duration} ${warmup}`,
    `${dataset.products} products in ${dataset.categories} categories, ${dataset.customers} ${dataset.customerSource} `
      + `customer${dataset.customers === 1 ? '' : 's'}`,
    '',
    `${'endpoint'.padEnd(17)} ${'route'.padEnd(28)} ${'req/s'.padStart(7)} ${'p50 ms'.padStart(9)} ${'p95 ms'.padStart(9)} `
      + `${'p99 ms'.padStart(9)} ${'max ms'.padStart(9)} ${'errors'.padStart(8)}`,
    ...Object.entries(result.endpoints).map(([name, s]) => row(name, s.route, s)),
    row('all', '', overall),
    '',
    `Dropped iterations: ${result.droppedIterations}, VUs: ${result.vus.allocated} allocated `
      + `(${result.vus.preallocated} preallocated, cap ${result.vus.cap}), received: ${(overall.bytesReceived / 1e6).toFixed(1)} MB`,
    result.valid ? 'Valid run: every threshold passed' : `NOT a valid run: ${result.problems.join('; ')}`,
  ];
  if (dataset.customerSource === 'registered') {
    lines.push('Customers were registered for this run, so "my orders" started empty; '
      + 'set CUSTOMER_EMAILS to seeded customers for runs you compare');
  }

  lines.push(`Saved ${file}`, '');
  return lines.join('\n');
}

function fixed(value) {
  return value === null ? '-' : value.toFixed(1);
}

function positive(name, fallback) {
  const raw = __ENV[name];
  const value = raw === undefined || raw === '' ? fallback : Number(raw);
  if (!(value > 0)) {
    throw new Error(`${name} must be a number above 0, not "${raw}"`);
  }

  return value;
}

// A duration in k6's own format: 30s, 3m, 1m30s or 500ms; 0 turns the warm-up off
function millis(name, text) {
  const units = { h: 3600000, m: 60000, s: 1000, ms: 1 };
  const pattern = /(\d+(?:\.\d+)?)(ms|h|m|s)/g;
  let total = 0;
  let consumed = 0;
  let match;
  while ((match = pattern.exec(text)) !== null && match.index === consumed) {
    total += Number(match[1]) * units[match[2]];
    consumed += match[0].length;
  }

  if (text === '0') {
    return 0;
  }

  if (consumed === 0 || consumed !== text.length) {
    throw new Error(`${name} must be a duration such as 30s, 3m or 1m30s, not "${text}"`);
  }

  return total;
}
