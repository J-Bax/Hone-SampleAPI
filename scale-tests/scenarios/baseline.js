import http from 'k6/http';
import { check, sleep } from 'k6';

// Baseline scenario: steady-state load test
// 50 virtual users for 30 seconds
export const options = {
  vus: 50,
  duration: '30s',
  thresholds: {
    http_req_duration: ['p(95)<500'],  // p95 under 500ms
    http_req_failed: ['rate<0.01'],     // error rate under 1%
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';

// Deterministic ID generation for reproducible traffic patterns.
// Same VU + iteration + salt always produces the same ID across runs.
function seededId(max, salt) {
  const h = ((__VU * 997 + __ITER * 8191 + salt * 127) * 2654435761) >>> 0;
  return (h % max) + 1;
}

export default function () {
  // ── Product endpoints ──

  // GET /api/products — list all products (intentionally slow, no pagination)
  const listRes = http.get(`${BASE_URL}/api/products`);
  check(listRes, {
    'list products: status 200': (r) => r.status === 200,
    'list products: has data': (r) => {
      const body = r.json();
      return Array.isArray(body) && body.length > 0;
    },
  });

  sleep(0.5);

  // GET /api/products/{id} — get a single product
  const randomId = seededId(100, 1);
  const getRes = http.get(`${BASE_URL}/api/products/${randomId}`);
  check(getRes, {
    'get product: status 200 or 404': (r) => r.status === 200 || r.status === 404,
  });

  sleep(0.5);

  // GET /api/products/search?q=Product — search products (intentionally loads all)
  const searchRes = http.get(`${BASE_URL}/api/products/search?q=Product`);
  check(searchRes, {
    'search: status 200': (r) => r.status === 200,
  });

  sleep(0.5);

  // GET /api/products/by-category/Electronics — filter by category (N+1 pattern)
  const categoryRes = http.get(`${BASE_URL}/api/products/by-category/Electronics`);
  check(categoryRes, {
    'category filter: status 200': (r) => r.status === 200,
  });

  sleep(0.5);

  // ── Review endpoints ──

  // GET /api/reviews/by-product/{id} — reviews for a product (loads all, filters in memory)
  const reviewProductId = seededId(500, 2);
  const reviewsRes = http.get(`${BASE_URL}/api/reviews/by-product/${reviewProductId}`);
  check(reviewsRes, {
    'reviews by product: status 200': (r) => r.status === 200,
  });

  sleep(0.3);

  // GET /api/reviews/average/{id} — average rating (loads all reviews for product)
  const avgRes = http.get(`${BASE_URL}/api/reviews/average/${reviewProductId}`);
  check(avgRes, {
    'average rating: status 200': (r) => r.status === 200,
  });

  sleep(0.3);

  // ── Cart flow ──

  const sessionId = `k6-session-${__VU}-${__ITER}`;

  // POST /api/cart — add item to cart
  const addCartRes = http.post(`${BASE_URL}/api/cart`,
    JSON.stringify({ sessionId, productId: randomId, quantity: 1 }),
    { headers: { 'Content-Type': 'application/json' } }
  );
  check(addCartRes, {
    'add to cart: status 200 or 201': (r) => r.status === 200 || r.status === 201,
  });

  sleep(0.3);

  // GET /api/cart/{sessionId} — get cart with N+1 product lookups
  const cartRes = http.get(`${BASE_URL}/api/cart/${sessionId}`);
  check(cartRes, {
    'get cart: status 200': (r) => r.status === 200,
  });

  sleep(0.3);

  // DELETE /api/cart/session/{sessionId} — clear cart (one-by-one deletes)
  http.del(`${BASE_URL}/api/cart/session/${sessionId}`);

  sleep(0.3);

  // ── Order flow ──

  // POST /api/orders — create order (N+1 product price lookups)
  const orderRes = http.post(`${BASE_URL}/api/orders`,
    JSON.stringify({
      customerName: `k6-user-${__VU}`,
      items: [
        { productId: seededId(100, 3), quantity: 1 },
        { productId: seededId(100, 4), quantity: 2 },
      ],
    }),
    { headers: { 'Content-Type': 'application/json' } }
  );
  check(orderRes, {
    'create order: status 201': (r) => r.status === 201,
  });

  sleep(0.3);

  // ── Razor Pages ──

  const homeRes = http.get(`${BASE_URL}/`);
  check(homeRes, {
    'home page: status 200': (r) => r.status === 200,
  });

  sleep(0.3);

  const productsPageRes = http.get(`${BASE_URL}/Products`);
  check(productsPageRes, {
    'products page: status 200': (r) => r.status === 200,
  });

  sleep(0.3);

  const detailPageRes = http.get(`${BASE_URL}/Products/Detail/${randomId}`);
  check(detailPageRes, {
    'product detail page: status 200': (r) => r.status === 200,
  });

  sleep(0.3);
}

export function handleSummary(data) {
  return {
    stdout: textSummary(data, { indent: '  ', enableColors: true }),
  };
}

// k6 built-in text summary helper
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
