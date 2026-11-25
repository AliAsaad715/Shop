// R5-12: each counted response has travelled through Nginx. No writes are retried by this read-only proof.
import http from 'k6/http';
import { Counter, Rate } from 'k6/metrics';

const answered = new Counter('answered_by');
const invalid = new Rate('invalid_response');
const api = (__ENV.API_URL || 'http://nginx').replace(/\/+$/, '');
const mode = __ENV.MODE || 'distribution';
const label = __ENV.LABEL || mode;
const expected = (__ENV.EXPECT_INSTANCES || 'api1,api2,api3').split(',');
const rate = Number(__ENV.RATE || 100);
if (!['distribution', 'continuity', 'comparison'].includes(mode) || !Number.isInteger(rate) || rate <= 0)
  throw new Error('MODE must be distribution, continuity or comparison, and RATE a positive integer');

export const options = {
  scenarios: {
    reads: mode === 'distribution'
      ? { executor: 'shared-iterations', vus: 10, iterations: 500, maxDuration: '1m' }
      : { executor: 'constant-arrival-rate', rate, timeUnit: '1s', duration: __ENV.DURATION || '45s',
          preAllocatedVUs: 30, maxVUs: 200 },
  },
  thresholds: {
    invalid_response: ['rate<0.01'], http_req_failed: ['rate<0.01'],
    ...(mode === 'distribution' ? {} : { dropped_iterations: ['count==0'] }),
    ...Object.fromEntries(expected.map(instance => [`answered_by{instance:${instance}}`, ['count>0']])),
  },
  summaryTrendStats: ['count', 'p(50)', 'p(95)', 'p(99)', 'max'],
  discardResponseBodies: true,
};

export function setup() {
  // The controller waits for this marker before stopping api2 during the continuity run.
  console.log('R5_LOAD_STARTED');
}

export default function () {
  const response = http.get(`${api}/api/v1/products?page=1&pageSize=20`,
    { timeout: '5s', tags: { name: 'GET /api/v1/products' } });
  const instance = response.headers['X-Instance'] || 'missing';
  answered.add(1, { instance });
  invalid.add(response.status !== 200 || !expected.includes(instance));
}

export function handleSummary(data) {
  const total = data.metrics.answered_by?.values.count || 0;
  const report = {
    label, mode, commit: __ENV.COMMIT || 'unknown', dataset: 'six demo products; read-only catalogue',
    rate: mode === 'distribution' ? null : rate, total,
    instances: Object.fromEntries(expected.map(instance => {
      const count = data.metrics[`answered_by{instance:${instance}}`]?.values.count || 0;
      return [instance, { count, percent: total ? Math.round(count / total * 10000) / 100 : 0 }];
    })),
    errorRate: data.metrics.http_req_failed?.values.rate || 0,
    invalidRate: data.metrics.invalid_response?.values.rate || 0,
    droppedIterations: data.metrics.dropped_iterations?.values.count || 0,
    latencyMs: data.metrics.http_req_duration?.values || {},
    thresholdsPassed: Object.values(data.metrics).every(metric =>
      Object.values(metric.thresholds || {}).every(threshold => threshold.ok)),
  };
  const json = JSON.stringify(report, null, 2) + '\n';
  return { stdout: json, [__ENV.RESULT_FILE || `/results/${label}.json`]: json };
}
