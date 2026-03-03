import http from 'k6/http';
import { check, sleep } from 'k6';

// Stress scenario: Products CRUD lifecycle
// Exercises POST, GET, PUT, DELETE on /api/products at high concurrency.
// Each VU iteration creates a product, reads it, updates it, then deletes it.
export const options = {
  stages: [
    { duration: '15s', target: 10 },   // Warm-up
    { duration: '30s', target: 50 },   // Normal load
    { duration: '30s', target: 100 },  // High load
    { duration: '30s', target: 200 },  // Stress load
    { duration: '15s', target: 0 },    // Cool-down
  ],
  thresholds: {
    http_req_duration: ['p(95)<1500'],  // p95 under 1.5s (writes are heavier)
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
  const uniqueName = `k6-stress-product-${__VU}-${__ITER}`;
  const price = (seededId(10000, 1) / 100).toFixed(2);
  const categories = ['Electronics', 'Clothing', 'Books', 'Home', 'Sports'];
  const category = categories[seededId(categories.length, 2) - 1];

  // ── CREATE ──
  const createRes = http.post(
    `${BASE_URL}/api/products`,
    JSON.stringify({
      name: uniqueName,
      description: `Stress test product created by VU ${__VU}`,
      price: parseFloat(price),
      category: category,
    }),
    JSON_HEADERS
  );
  check(createRes, {
    'create product: status 201': (r) => r.status === 201,
  });

  // Extract the created product ID for subsequent operations
  let productId = null;
  if (createRes.status === 201) {
    try {
      productId = createRes.json('id');
    } catch (_) {
      // If parsing fails, skip remaining CRUD ops
    }
  }

  sleep(0.2);

  if (productId) {
    // ── READ ──
    const getRes = http.get(`${BASE_URL}/api/products/${productId}`);
    check(getRes, {
      'get created product: status 200': (r) => r.status === 200,
      'get created product: name matches': (r) => {
        try { return r.json('name') === uniqueName; } catch (_) { return false; }
      },
    });

    sleep(0.2);

    // ── UPDATE ──
    const updateRes = http.put(
      `${BASE_URL}/api/products/${productId}`,
      JSON.stringify({
        id: productId,
        name: `${uniqueName}-updated`,
        description: 'Updated by stress test',
        price: parseFloat(price) + 1.00,
        category: category,
      }),
      JSON_HEADERS
    );
    check(updateRes, {
      'update product: status 200 or 204': (r) => r.status === 200 || r.status === 204,
    });

    sleep(0.2);

    // ── DELETE ──
    const deleteRes = http.del(`${BASE_URL}/api/products/${productId}`);
    check(deleteRes, {
      'delete product: status 200 or 204': (r) => r.status === 200 || r.status === 204,
    });
  }

  sleep(0.3);
}

// Teardown: clean up any orphaned stress-test products that survived
// (e.g. if a VU crashed between create and delete).
export function teardown() {
  const searchRes = http.get(`${BASE_URL}/api/products/search?q=k6-stress-product`);
  if (searchRes.status === 200) {
    try {
      const products = searchRes.json();
      if (Array.isArray(products)) {
        for (const p of products) {
          if (p.name && p.name.startsWith('k6-stress-product')) {
            http.del(`${BASE_URL}/api/products/${p.id}`);
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
