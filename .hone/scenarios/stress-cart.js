import http from 'k6/http';
import { check, sleep } from 'k6';

// Stress scenario: Cart operations lifecycle
// Exercises POST (add), GET, PUT (update qty), DELETE (item), DELETE (session)
// on /api/cart. Each VU iteration runs a full cart session: add items, read,
// update a quantity, remove an item, then clear the session.
export const options = {
  stages: [
    { duration: '15s', target: 10 },   // Warm-up
    { duration: '30s', target: 50 },   // Normal load
    { duration: '30s', target: 100 },  // High load
    { duration: '30s', target: 200 },  // Stress load
    { duration: '15s', target: 0 },    // Cool-down
  ],
  thresholds: {
    http_req_duration: ['p(95)<1000'],  // p95 under 1s (cart ops are lighter)
    http_req_failed: ['rate<0.02'],      // error rate under 2%
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
  const sessionId = `k6-stress-cart-${__VU}-${__ITER}`;

  // ── ADD ITEM 1 ──
  const add1Res = http.post(
    `${BASE_URL}/api/cart`,
    JSON.stringify({
      sessionId,
      productId: seededId(100, 1),
      quantity: seededId(3, 10),
    }),
    JSON_HEADERS
  );
  check(add1Res, {
    'add item 1: status 200 or 201': (r) => r.status === 200 || r.status === 201,
  });

  let item1Id = null;
  if (add1Res.status === 200 || add1Res.status === 201) {
    try { item1Id = add1Res.json('id'); } catch (_) {}
  }

  sleep(0.15);

  // ── ADD ITEM 2 ──
  const add2Res = http.post(
    `${BASE_URL}/api/cart`,
    JSON.stringify({
      sessionId,
      productId: seededId(100, 2),
      quantity: seededId(5, 20),
    }),
    JSON_HEADERS
  );
  check(add2Res, {
    'add item 2: status 200 or 201': (r) => r.status === 200 || r.status === 201,
  });

  let item2Id = null;
  if (add2Res.status === 200 || add2Res.status === 201) {
    try { item2Id = add2Res.json('id'); } catch (_) {}
  }

  sleep(0.15);

  // ── ADD ITEM 3 ──
  const add3Res = http.post(
    `${BASE_URL}/api/cart`,
    JSON.stringify({
      sessionId,
      productId: seededId(100, 3),
      quantity: 1,
    }),
    JSON_HEADERS
  );
  check(add3Res, {
    'add item 3: status 200 or 201': (r) => r.status === 200 || r.status === 201,
  });

  sleep(0.15);

  // ── READ CART ──
  const cartRes = http.get(`${BASE_URL}/api/cart/${sessionId}`);
  check(cartRes, {
    'get cart: status 200': (r) => r.status === 200,
    'get cart: has items': (r) => {
      try { return r.json().length >= 1; } catch (_) { return false; }
    },
  });

  sleep(0.15);

  // ── UPDATE QUANTITY (item 1) ──
  if (item1Id) {
    const updateRes = http.put(
      `${BASE_URL}/api/cart/${item1Id}`,
      JSON.stringify(10),
      JSON_HEADERS
    );
    check(updateRes, {
      'update quantity: status 200 or 204': (r) => r.status === 200 || r.status === 204,
    });
  }

  sleep(0.15);

  // ── DELETE SINGLE ITEM (item 2) ──
  if (item2Id) {
    const delItemRes = http.del(`${BASE_URL}/api/cart/${item2Id}`);
    check(delItemRes, {
      'delete item: status 200 or 204': (r) => r.status === 200 || r.status === 204,
    });
  }

  sleep(0.15);

  // ── CLEAR SESSION ──
  const clearRes = http.del(`${BASE_URL}/api/cart/session/${sessionId}`);
  check(clearRes, {
    'clear cart session: status 200 or 204': (r) => r.status === 200 || r.status === 204,
  });

  sleep(0.3);
}

// Teardown: clear any orphaned stress-test cart sessions.
export function teardown() {
  // Clean up sessions for a range of VUs (best-effort)
  for (let vu = 1; vu <= 200; vu++) {
    for (let iter = 0; iter < 5; iter++) {
      http.del(`${BASE_URL}/api/cart/session/k6-stress-cart-${vu}-${iter}`);
    }
  }
}

export function handleSummary(data) {
  return {
    stdout: textSummary(data, { indent: '  ', enableColors: true }),
  };
}

import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
