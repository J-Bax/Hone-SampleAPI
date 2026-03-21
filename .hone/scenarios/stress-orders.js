import http from 'k6/http';
import { check, sleep } from 'k6';

// Stress scenario: Orders lifecycle
// Exercises POST, GET (by id, by customer), and PUT status on /api/orders.
// Each VU iteration creates an order, fetches it, queries by customer, then
// advances status through Pending → Shipped → Delivered.
export const options = {
  stages: [
    { duration: '15s', target: 10 },   // Warm-up
    { duration: '30s', target: 50 },   // Normal load
    { duration: '30s', target: 100 },  // High load
    { duration: '30s', target: 200 },  // Stress load
    { duration: '15s', target: 0 },    // Cool-down
  ],
  thresholds: {
    http_req_duration: ['p(95)<2000'],  // p95 under 2s (order creation is heavy)
    http_req_failed: ['rate<0.05'],      // error rate under 5%
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const JSON_HEADERS = { headers: { 'Content-Type': 'application/json' } };

// Deterministic ID generation for reproducible traffic patterns.
function seededId(max, salt) {
  const h = ((__VU * 997 + __ITER * 8191 + salt * 127) * 2654435761) >>> 0;
  return (h % max) + 1;
}

// Verify that products exist before hammering order creation.
export function setup() {
  const res = http.get(`${BASE_URL}/api/products`);
  const ok = res.status === 200;
  if (ok) {
    try {
      const products = res.json();
      return { productCount: Array.isArray(products) ? products.length : 0 };
    } catch (_) {
      return { productCount: 0 };
    }
  }
  return { productCount: 0 };
}

export default function (data) {
  const customerName = `k6-stress-customer-${__VU}`;
  const itemCount = seededId(3, 1); // 1-3 items per order

  // Build order items referencing seeded product IDs
  const items = [];
  for (let i = 0; i < itemCount; i++) {
    items.push({
      productId: seededId(Math.min(data.productCount || 100, 100), i + 10),
      quantity: seededId(5, i + 20),
    });
  }

  // ── CREATE ORDER ──
  const createRes = http.post(
    `${BASE_URL}/api/orders`,
    JSON.stringify({ customerName, items }),
    JSON_HEADERS
  );
  check(createRes, {
    'create order: status 201': (r) => r.status === 201,
  });

  let orderId = null;
  if (createRes.status === 201) {
    try {
      orderId = createRes.json('id');
    } catch (_) {}
  }

  sleep(0.2);

  if (orderId) {
    // ── GET ORDER BY ID ──
    const getRes = http.get(`${BASE_URL}/api/orders/${orderId}`);
    check(getRes, {
      'get order by id: status 200': (r) => r.status === 200,
    });

    sleep(0.2);

    // ── GET ORDERS BY CUSTOMER ──
    const byCustomerRes = http.get(
      `${BASE_URL}/api/orders/by-customer/${encodeURIComponent(customerName)}`
    );
    check(byCustomerRes, {
      'orders by customer: status 200': (r) => r.status === 200,
      'orders by customer: has results': (r) => {
        try { return r.json().length > 0; } catch (_) { return false; }
      },
    });

    sleep(0.2);

    // ── UPDATE STATUS: Shipped ──
    const shippedRes = http.put(
      `${BASE_URL}/api/orders/${orderId}/status`,
      JSON.stringify({ status: 'Shipped' }),
      JSON_HEADERS
    );
    check(shippedRes, {
      'update to Shipped: status 200 or 204': (r) => r.status === 200 || r.status === 204,
    });

    sleep(0.2);

    // ── UPDATE STATUS: Delivered ──
    const deliveredRes = http.put(
      `${BASE_URL}/api/orders/${orderId}/status`,
      JSON.stringify({ status: 'Delivered' }),
      JSON_HEADERS
    );
    check(deliveredRes, {
      'update to Delivered: status 200 or 204': (r) => r.status === 200 || r.status === 204,
    });
  }

  sleep(0.3);
}

export function handleSummary(data) {
  return {
    stdout: textSummary(data, { indent: '  ', enableColors: true }),
  };
}

import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
