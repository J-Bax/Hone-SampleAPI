import http from 'k6/http';
import { check, sleep } from 'k6';

// Stress-UI scenario: isolates Razor Page server-side rendering under load.
// Simulates a full user journey through the storefront UI (no API calls):
// home → browse products → product detail → add to cart → cart → checkout → orders.
// Diagnostic only (not optimization-gated) — surfaces page rendering bottlenecks.
export const options = {
  stages: [
    { duration: '15s', target: 10 },   // Warm-up
    { duration: '30s', target: 50 },   // Normal load
    { duration: '30s', target: 100 },  // High load
    { duration: '30s', target: 200 },  // Stress load
    { duration: '15s', target: 0 },    // Cool-down
  ],
  thresholds: {
    http_req_duration: ['p(95)<3000'],  // p95 under 3s (page rendering is heavier)
    http_req_failed: ['rate<0.05'],      // error rate under 5%
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';

// Deterministic ID generation for reproducible traffic patterns.
function seededId(max, salt) {
  const h = ((__VU * 997 + __ITER * 8191 + salt * 127) * 2654435761) >>> 0;
  return (h % max) + 1;
}

export default function () {
  const productId = seededId(100, 1);
  const customerName = `k6-ui-${__VU}`;

  // Step 1: Home page (loads 12 shuffled products, all categories, 5 recent reviews)
  const homeRes = http.get(`${BASE_URL}/`);
  check(homeRes, {
    'home: status 200': (r) => r.status === 200,
  });
  sleep(0.2);

  // Step 2: Products catalog (loads all products, filters in-memory, paginates)
  const productsRes = http.get(`${BASE_URL}/Products`);
  check(productsRes, {
    'products: status 200': (r) => r.status === 200,
  });
  sleep(0.2);

  // Step 3: Search products (in-memory search across all products)
  const searchRes = http.get(`${BASE_URL}/Products?q=Product`);
  check(searchRes, {
    'search: status 200': (r) => r.status === 200,
  });
  sleep(0.2);

  // Step 4: Category filter
  const categoryRes = http.get(`${BASE_URL}/Products?category=Electronics`);
  check(categoryRes, {
    'category filter: status 200': (r) => r.status === 200,
  });
  sleep(0.2);

  // Step 5: Product detail page (product + reviews + related products)
  const detailRes = http.get(`${BASE_URL}/Products/Detail/${productId}`);
  check(detailRes, {
    'detail: status 200': (r) => r.status === 200,
  });
  sleep(0.2);

  // Step 6: Add to cart via product detail form POST (sets CartSessionId cookie)
  const addToCartRes = http.post(
    `${BASE_URL}/Products/Detail/${productId}`,
    { productId: String(productId), quantity: '2' }
  );
  check(addToCartRes, {
    'add to cart: status 200': (r) => r.status === 200,
  });
  sleep(0.2);

  // Add a second product to make cart operations more realistic
  const secondProductId = seededId(100, 2);
  const addSecondRes = http.post(
    `${BASE_URL}/Products/Detail/${secondProductId}`,
    { productId: String(secondProductId), quantity: '1' }
  );
  check(addSecondRes, {
    'add second item: status 200': (r) => r.status === 200,
  });
  sleep(0.2);

  // Step 7: View cart page (N+1 product lookups in LoadCart)
  const cartRes = http.get(`${BASE_URL}/Cart`);
  check(cartRes, {
    'cart page: status 200': (r) => r.status === 200,
  });
  sleep(0.2);

  // Step 8: View checkout page (same N+1 in LoadCartSummary)
  const checkoutRes = http.get(`${BASE_URL}/Checkout`);
  check(checkoutRes, {
    'checkout page: status 200': (r) => r.status === 200,
  });
  sleep(0.2);

  // Step 9: Submit order via checkout (heaviest: N+1 + per-item SaveChanges + cart clear)
  const submitRes = http.post(
    `${BASE_URL}/Checkout`,
    { customerName: customerName }
  );
  check(submitRes, {
    'checkout submit: status 200': (r) => r.status === 200,
  });
  sleep(0.2);

  // Step 10: View order history (loads all orders, filters in-memory, N+1 product names)
  const ordersRes = http.get(
    `${BASE_URL}/Orders?customer=${encodeURIComponent(customerName)}`
  );
  check(ordersRes, {
    'orders page: status 200': (r) => r.status === 200,
  });
}

export function teardown() {
  // Checkout POST clears the cart on success, so no cart cleanup needed.
  // Orders accumulate intentionally (same as baseline's API order flow).
  // If checkout failed mid-flow, orphaned cart items use random cookie-based
  // session IDs that we can't enumerate — acceptable for a diagnostic scenario.
}

export function handleSummary(data) {
  return {
    stdout: textSummary(data, { indent: '  ', enableColors: true }),
  };
}

import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
