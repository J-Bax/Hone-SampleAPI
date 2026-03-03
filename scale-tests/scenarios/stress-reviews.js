import http from 'k6/http';
import { check, sleep } from 'k6';

// Stress scenario: Reviews CRUD lifecycle
// Exercises GET (all, by id, by product, average), POST, DELETE on /api/reviews.
// Each VU iteration creates a review, reads it back, queries aggregations, then
// deletes it.
export const options = {
  stages: [
    { duration: '15s', target: 10 },   // Warm-up
    { duration: '30s', target: 50 },   // Normal load
    { duration: '30s', target: 100 },  // High load
    { duration: '30s', target: 200 },  // Stress load
    { duration: '15s', target: 0 },    // Cool-down
  ],
  thresholds: {
    http_req_duration: ['p(95)<1500'],  // p95 under 1.5s
    http_req_failed: ['rate<0.03'],      // error rate under 3%
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const JSON_HEADERS = { headers: { 'Content-Type': 'application/json' } };

// Deterministic ID generation for reproducible traffic patterns.
function seededId(max, salt) {
  const h = ((__VU * 997 + __ITER * 8191 + salt * 127) * 2654435761) >>> 0;
  return (h % max) + 1;
}

export default function () {
  const productId = seededId(100, 1);
  const customerName = `k6-stress-reviewer-${__VU}-${__ITER}`;
  const rating = seededId(5, 2);

  // ── CREATE REVIEW ──
  const createRes = http.post(
    `${BASE_URL}/api/reviews`,
    JSON.stringify({
      productId,
      customerName,
      rating,
      comment: `Stress test review from VU ${__VU} iteration ${__ITER}`,
    }),
    JSON_HEADERS
  );
  check(createRes, {
    'create review: status 201': (r) => r.status === 201,
  });

  let reviewId = null;
  if (createRes.status === 201) {
    try { reviewId = createRes.json('id'); } catch (_) {}
  }

  sleep(0.2);

  // ── GET ALL REVIEWS ──
  const allRes = http.get(`${BASE_URL}/api/reviews`);
  check(allRes, {
    'list all reviews: status 200': (r) => r.status === 200,
  });

  sleep(0.2);

  if (reviewId) {
    // ── GET REVIEW BY ID ──
    const getRes = http.get(`${BASE_URL}/api/reviews/${reviewId}`);
    check(getRes, {
      'get review by id: status 200': (r) => r.status === 200,
      'get review by id: rating matches': (r) => {
        try { return r.json('rating') === rating; } catch (_) { return false; }
      },
    });

    sleep(0.2);
  }

  // ── GET REVIEWS BY PRODUCT ──
  const byProductRes = http.get(`${BASE_URL}/api/reviews/by-product/${productId}`);
  check(byProductRes, {
    'reviews by product: status 200': (r) => r.status === 200,
  });

  sleep(0.2);

  // ── GET AVERAGE RATING ──
  const avgRes = http.get(`${BASE_URL}/api/reviews/average/${productId}`);
  check(avgRes, {
    'average rating: status 200': (r) => r.status === 200,
  });

  sleep(0.2);

  // ── DELETE REVIEW ──
  if (reviewId) {
    const deleteRes = http.del(`${BASE_URL}/api/reviews/${reviewId}`);
    check(deleteRes, {
      'delete review: status 200 or 204': (r) => r.status === 200 || r.status === 204,
    });
  }

  sleep(0.3);
}

// Teardown: clean up any orphaned stress-test reviews.
export function teardown() {
  const allRes = http.get(`${BASE_URL}/api/reviews`);
  if (allRes.status === 200) {
    try {
      const reviews = allRes.json();
      if (Array.isArray(reviews)) {
        for (const r of reviews) {
          if (r.customerName && r.customerName.startsWith('k6-stress-reviewer')) {
            http.del(`${BASE_URL}/api/reviews/${r.id}`);
          }
        }
      }
    } catch (_) {
      // Best-effort cleanup
    }
  }
}

export function handleSummary(data) {
  return {
    stdout: textSummary(data, { indent: '  ', enableColors: true }),
  };
}

import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
