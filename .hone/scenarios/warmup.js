import http from 'k6/http';
import { check, sleep } from 'k6';
import { createScenarioContext, seededCategory, seededProductId, sessionId } from './shared.js';

// Warmup remains experiment-scoped priming only. Per-measured-run cleanup now
// lives in scenario setup()/teardown() through the target's /diag/runs endpoints.
export const options = {
  vus: 5,
  duration: '12s',
  thresholds: {
    http_req_failed: ['rate<0.05'],
  },
};

export function setup() {
  return createScenarioContext('warmup');
}

export default function (context) {
  const productId = seededProductId(context, 1);
  const category = seededCategory(context, 2);
  const cartSession = sessionId(context, 'warmup');

  const responses = http.batch([
    ['GET', `${context.baseUrl}/health`],
    ['GET', `${context.baseUrl}/api/categories`],
    ['GET', `${context.baseUrl}/api/products`],
    ['GET', `${context.baseUrl}/api/products/by-category/${encodeURIComponent(category)}`],
    ['GET', `${context.baseUrl}/api/reviews/average/${productId}`],
    ['GET', `${context.baseUrl}/Products/Detail/${productId}`],
    ['GET', `${context.baseUrl}/api/cart/${cartSession}`],
  ]);

  check(responses[0], { 'health: status 200': (response) => response.status === 200 });
  check(responses[1], { 'categories: status 200': (response) => response.status === 200 });
  check(responses[2], { 'products: status 200': (response) => response.status === 200 });
  check(responses[3], { 'category filter: status 200': (response) => response.status === 200 });
  check(responses[4], { 'average rating: status 200': (response) => response.status === 200 });
  check(responses[5], { 'detail page: status 200': (response) => response.status === 200 });
  check(responses[6], { 'empty cart lookup: status 200': (response) => response.status === 200 });

  sleep(0.4);
}
