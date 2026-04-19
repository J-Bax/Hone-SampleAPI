import http from 'k6/http';
import { check, sleep } from 'k6';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
import { cleanupScenario, prepareScenario } from './diagnostics.js';
import { JSON_PARAMS, seededProductId, tryJson } from './shared.js';
import { buildReviewPayload } from './payloads.js';

// Review lifecycle with exact run-scoped create/read/delete semantics.
export const options = {
  stages: [
    { duration: '15s', target: 10 },
    { duration: '30s', target: 50 },
    { duration: '30s', target: 100 },
    { duration: '30s', target: 180 },
    { duration: '15s', target: 0 },
  ],
  thresholds: {
    http_req_duration: ['p(95)<1200'],
    http_req_failed: ['rate<0.03'],
  },
};

export function setup() {
  return prepareScenario('stress-reviews');
}

export default function (context) {
  const productId = seededProductId(context, 1);
  const reviewPayload = buildReviewPayload(context, productId, ((__VU + __ITER) % 5) + 1, 'stress');

  const createResponse = http.post(
    `${context.baseUrl}/api/reviews`,
    JSON.stringify(reviewPayload),
    JSON_PARAMS,
  );
  check(createResponse, {
    'create review: status 201': (response) => response.status === 201,
  });
  const reviewId = tryJson(createResponse, 'id', null);

  const allReviewsResponse = http.get(`${context.baseUrl}/api/reviews`);
  check(allReviewsResponse, {
    'list reviews: status 200': (response) => response.status === 200,
  });

  if (reviewId) {
    const getResponse = http.get(`${context.baseUrl}/api/reviews/${reviewId}`);
    check(getResponse, {
      'get review: status 200': (response) => response.status === 200,
    });
  }

  const byProductResponse = http.get(`${context.baseUrl}/api/reviews/by-product/${productId}`);
  check(byProductResponse, {
    'reviews by product: status 200': (response) => response.status === 200,
  });

  const averageResponse = http.get(`${context.baseUrl}/api/reviews/average/${productId}`);
  check(averageResponse, {
    'average review: status 200': (response) => response.status === 200,
  });

  if (reviewId) {
    const deleteResponse = http.del(`${context.baseUrl}/api/reviews/${reviewId}`);
    check(deleteResponse, {
      'delete review: status 204': (response) => response.status === 204,
    });
  }

  sleep(0.2);
}

export function teardown(context) {
  cleanupScenario(context);
}

export function handleSummary(data) {
  return {
    stdout: textSummary(data, { indent: '  ', enableColors: true }),
  };
}
