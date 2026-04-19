import http from 'k6/http';
import { check, sleep } from 'k6';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
import { createScenarioContext, seededCategory, seededProductId, tryJson } from './shared.js';

// Read-only storefront browsing path for catalog/search/detail pressure.
export const options = {
  stages: [
    { duration: '15s', target: 20 },
    { duration: '30s', target: 60 },
    { duration: '30s', target: 120 },
    { duration: '30s', target: 200 },
    { duration: '15s', target: 0 },
  ],
  thresholds: {
    http_req_duration: ['p(95)<1200'],
    http_req_failed: ['rate<0.02'],
  },
};

export function setup() {
  return createScenarioContext('stress-storefront');
}

export default function (context) {
  const category = seededCategory(context, 1);
  const productId = seededProductId(context, 2);
  const page = ((__ITER % 4) + 1);

  const homeResponse = http.get(`${context.baseUrl}/`);
  check(homeResponse, {
    'home: status 200': (response) => response.status === 200,
  });

  const browseResponse = http.get(`${context.baseUrl}/Products?page=${page}`);
  check(browseResponse, {
    'browse page: status 200': (response) => response.status === 200,
  });

  const categoryPageResponse = http.get(
    `${context.baseUrl}/Products?category=${encodeURIComponent(category)}&page=${page}`,
  );
  check(categoryPageResponse, {
    'category page: status 200': (response) => response.status === 200,
  });

  const searchResponse = http.get(
    `${context.baseUrl}/api/products/search?q=${encodeURIComponent('Product 00')}`,
  );
  check(searchResponse, {
    'search api: status 200': (response) => response.status === 200,
    'search api: has matches': (response) => (tryJson(response, null, []).length || 0) > 0,
  });

  const categoryApiResponse = http.get(
    `${context.baseUrl}/api/products/by-category/${encodeURIComponent(category)}`,
  );
  check(categoryApiResponse, {
    'category api: status 200': (response) => response.status === 200,
  });

  const detailResponse = http.get(`${context.baseUrl}/Products/Detail/${productId}`);
  check(detailResponse, {
    'detail page: status 200': (response) => response.status === 200,
  });

  const averageResponse = http.get(`${context.baseUrl}/api/reviews/average/${productId}`);
  check(averageResponse, {
    'average rating: status 200': (response) => response.status === 200,
  });

  sleep(0.25);
}

export function handleSummary(data) {
  return {
    stdout: textSummary(data, { indent: '  ', enableColors: true }),
  };
}
