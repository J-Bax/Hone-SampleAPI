import http from 'k6/http';
import { check, sleep } from 'k6';

// Spike scenario: sudden burst to test resilience
// Idle → instant 100 VUs → back to idle
export const options = {
  stages: [
    { duration: '5s', target: 1 },     // Idle baseline
    { duration: '1s', target: 100 },    // Instant spike!
    { duration: '30s', target: 100 },   // Sustained spike
    { duration: '1s', target: 1 },      // Drop back
    { duration: '10s', target: 1 },     // Recovery period
  ],
  thresholds: {
    http_req_duration: ['p(95)<2000'],   // p95 under 2s (very lenient for spike)
    http_req_failed: ['rate<0.10'],       // error rate under 10%
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';

// Deterministic ID generation for reproducible traffic patterns.
function seededId(max, salt) {
  const h = ((__VU * 997 + __ITER * 8191 + salt * 127) * 2654435761) >>> 0;
  return (h % max) + 1;
}

export default function () {
  // Hit the heaviest endpoints during spike — including marketplace features
  const randomId = seededId(100, 1);

  const listRes = http.get(`${BASE_URL}/api/products`);
  check(listRes, {
    'list: status 200': (r) => r.status === 200,
  });

  const searchRes = http.get(`${BASE_URL}/api/products/search?q=Product`);
  check(searchRes, {
    'search: status 200': (r) => r.status === 200,
  });

  // Reviews — heavy N+1 endpoint
  const reviewsRes = http.get(`${BASE_URL}/api/reviews/by-product/${randomId}`);
  check(reviewsRes, {
    'reviews: status 200': (r) => r.status === 200,
  });

  // Razor Pages — each loads entire tables and filters in memory
  const homeRes = http.get(`${BASE_URL}/`);
  check(homeRes, {
    'home page: status 200': (r) => r.status === 200,
  });

  const detailRes = http.get(`${BASE_URL}/Products/Detail/${randomId}`);
  check(detailRes, {
    'detail page: status 200': (r) => r.status === 200,
  });

  sleep(0.2);
}

export function handleSummary(data) {
  return {
    stdout: textSummary(data, { indent: '  ', enableColors: true }),
  };
}

import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
