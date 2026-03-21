import http from 'k6/http';
import { check, sleep } from 'k6';

// Warmup scenario: short burst to prime JIT, DB connections, and caches
// before the measured baseline run begins.
export const options = {
  vus: 5,
  duration: '10s',
  thresholds: {
    http_req_failed: ['rate<0.05'],
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';

export default function () {
  const productsRes = http.get(`${BASE_URL}/api/products`);
  check(productsRes, { 'products: status 200': (r) => r.status === 200 });

  const reviewsRes = http.get(`${BASE_URL}/api/reviews`);
  check(reviewsRes, { 'reviews: status 200': (r) => r.status === 200 });

  const ordersRes = http.get(`${BASE_URL}/api/orders`);
  check(ordersRes, { 'orders: status 200': (r) => r.status === 200 });

  const categoriesRes = http.get(`${BASE_URL}/api/categories`);
  check(categoriesRes, { 'categories: status 200': (r) => r.status === 200 });

  const sessionId = `warmup-${__VU}-${__ITER}`;
  const cartRes = http.get(`${BASE_URL}/api/cart/${sessionId}`);
  check(cartRes, { 'cart: status 200': (r) => r.status === 200 });

  sleep(0.5);
}
