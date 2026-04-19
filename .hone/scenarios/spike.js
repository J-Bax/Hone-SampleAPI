import http from 'k6/http';
import { check, sleep } from 'k6';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
import { createScenarioContext, seededCategory, seededProductId } from './shared.js';

// Read-only burst against the busiest catalog and page-rendering endpoints.
export const options = {
  stages: [
    { duration: '5s', target: 1 },
    { duration: '1s', target: 100 },
    { duration: '30s', target: 100 },
    { duration: '1s', target: 1 },
    { duration: '10s', target: 1 },
  ],
  thresholds: {
    http_req_duration: ['p(95)<2000'],
    http_req_failed: ['rate<0.05'],
  },
};

export function setup() {
  return createScenarioContext('spike');
}

export default function (context) {
  const productId = seededProductId(context, 1);
  const category = seededCategory(context, 2);

  const responses = http.batch([
    ['GET', `${context.baseUrl}/api/products`],
    ['GET', `${context.baseUrl}/api/products/search?q=${encodeURIComponent('Product 0')}`],
    ['GET', `${context.baseUrl}/api/products/by-category/${encodeURIComponent(category)}`],
    ['GET', `${context.baseUrl}/`],
    ['GET', `${context.baseUrl}/Products?page=1`],
    ['GET', `${context.baseUrl}/Products/Detail/${productId}`],
    ['GET', `${context.baseUrl}/api/reviews/average/${productId}`],
  ]);

  check(responses[0], { 'products list: status 200': (response) => response.status === 200 });
  check(responses[1], { 'search api: status 200': (response) => response.status === 200 });
  check(responses[2], { 'category api: status 200': (response) => response.status === 200 });
  check(responses[3], { 'home page: status 200': (response) => response.status === 200 });
  check(responses[4], { 'products page: status 200': (response) => response.status === 200 });
  check(responses[5], { 'detail page: status 200': (response) => response.status === 200 });
  check(responses[6], { 'average rating: status 200': (response) => response.status === 200 });

  sleep(0.1);
}

export function handleSummary(data) {
  return {
    stdout: textSummary(data, { indent: '  ', enableColors: true }),
  };
}
