import http from 'k6/http';
import { check, sleep } from 'k6';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
import { cleanupScenario, prepareScenario } from './diagnostics.js';
import { JSON_PARAMS, tryJson } from './shared.js';
import { buildProductPayload } from './payloads.js';

// Admin/catalog CRUD lifecycle with exact teardown cleanup.
export const options = {
  stages: [
    { duration: '15s', target: 10 },
    { duration: '30s', target: 50 },
    { duration: '30s', target: 100 },
    { duration: '30s', target: 180 },
    { duration: '15s', target: 0 },
  ],
  thresholds: {
    http_req_duration: ['p(95)<1500'],
    http_req_failed: ['rate<0.03'],
  },
};

export function setup() {
  return prepareScenario('stress-products');
}

export default function (context) {
  const productPayload = buildProductPayload(context, __VU + __ITER + 1);

  const createResponse = http.post(
    `${context.baseUrl}/api/products`,
    JSON.stringify(productPayload),
    JSON_PARAMS,
  );
  check(createResponse, {
    'create product: status 201': (response) => response.status === 201,
  });
  const productId = tryJson(createResponse, 'id', null);

  if (productId) {
    const getResponse = http.get(`${context.baseUrl}/api/products/${productId}`);
    check(getResponse, {
      'get product: status 200': (response) => response.status === 200,
    });

    const updateResponse = http.put(
      `${context.baseUrl}/api/products/${productId}`,
      JSON.stringify({
        id: productId,
        name: `${productPayload.name}-updated`,
        description: `${productPayload.description} updated`,
        price: Number((productPayload.price + 2.5).toFixed(2)),
        category: productPayload.category,
      }),
      JSON_PARAMS,
    );
    check(updateResponse, {
      'update product: status 204': (response) => response.status === 204,
    });

    const searchResponse = http.get(
      `${context.baseUrl}/api/products/search?q=${encodeURIComponent(context.scopePrefix)}`,
    );
    check(searchResponse, {
      'search tagged products: status 200': (response) => response.status === 200,
      'search tagged products: finds match': (response) => (tryJson(response, null, []).length || 0) > 0,
    });

    const deleteResponse = http.del(`${context.baseUrl}/api/products/${productId}`);
    check(deleteResponse, {
      'delete product: status 204': (response) => response.status === 204,
    });
  }

  sleep(0.25);
}

export function teardown(context) {
  cleanupScenario(context);
}

export function handleSummary(data) {
  return {
    stdout: textSummary(data, { indent: '  ', enableColors: true }),
  };
}
