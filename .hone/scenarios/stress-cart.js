import http from 'k6/http';
import { check, sleep } from 'k6';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
import { cleanupScenario, prepareScenario } from './diagnostics.js';
import {
  JSON_PARAMS,
  seededProductId,
  sessionId,
  tryJson,
  useCartSession,
} from './shared.js';
import { buildCartItemRequest } from './payloads.js';

// Cart lifecycle with exact run-scoped sessions and teardown cleanup.
export const options = {
  stages: [
    { duration: '15s', target: 10 },
    { duration: '30s', target: 50 },
    { duration: '30s', target: 100 },
    { duration: '30s', target: 200 },
    { duration: '15s', target: 0 },
  ],
  thresholds: {
    http_req_duration: ['p(95)<1000'],
    http_req_failed: ['rate<0.02'],
  },
};

export function setup() {
  return prepareScenario('stress-cart');
}

export default function (context) {
  const cartSession = sessionId(context, 'cart');
  const firstProductId = seededProductId(context, 1);
  const secondProductId = seededProductId(context, 2);
  const thirdProductId = seededProductId(context, 3);
  useCartSession(context, cartSession);

  const addFirstResponse = http.post(
    `${context.baseUrl}/api/cart`,
    JSON.stringify(buildCartItemRequest(cartSession, firstProductId, 1)),
    JSON_PARAMS,
  );
  check(addFirstResponse, {
    'add first cart item: status 200 or 201': (response) => response.status === 200 || response.status === 201,
  });
  const firstCartItemId = tryJson(addFirstResponse, 'id', null);

  const addSecondResponse = http.post(
    `${context.baseUrl}/api/cart`,
    JSON.stringify(buildCartItemRequest(cartSession, secondProductId, 2)),
    JSON_PARAMS,
  );
  check(addSecondResponse, {
    'add second cart item: status 200 or 201': (response) => response.status === 200 || response.status === 201,
  });
  const secondCartItemId = tryJson(addSecondResponse, 'id', null);

  const addThirdResponse = http.post(
    `${context.baseUrl}/api/cart`,
    JSON.stringify(buildCartItemRequest(cartSession, thirdProductId, 1)),
    JSON_PARAMS,
  );
  check(addThirdResponse, {
    'add third cart item: status 200 or 201': (response) => response.status === 200 || response.status === 201,
  });

  const cartResponse = http.get(`${context.baseUrl}/api/cart/${cartSession}`);
  check(cartResponse, {
    'cart api: status 200': (response) => response.status === 200,
    'cart api: has three items': (response) => (tryJson(response, 'itemCount', 0) || 0) >= 3,
  });

  if (firstCartItemId) {
    const updateResponse = http.put(
      `${context.baseUrl}/api/cart/${firstCartItemId}`,
      JSON.stringify(4),
      JSON_PARAMS,
    );
    check(updateResponse, {
      'update cart quantity: status 204': (response) => response.status === 204,
    });
  }

  if (secondCartItemId) {
    const removeResponse = http.del(`${context.baseUrl}/api/cart/${secondCartItemId}`);
    check(removeResponse, {
      'remove cart item: status 204': (response) => response.status === 204,
    });
  }

  const cartPageResponse = http.get(`${context.baseUrl}/Cart`);
  check(cartPageResponse, {
    'cart page: status 200': (response) => response.status === 200,
    'cart page: shopping cart title': (response) => response.body.includes('Shopping Cart'),
  });

  const clearResponse = http.del(`${context.baseUrl}/api/cart/session/${cartSession}`);
  check(clearResponse, {
    'clear cart session: status 204': (response) => response.status === 204,
  });

  const emptyCartResponse = http.get(`${context.baseUrl}/api/cart/${cartSession}`);
  check(emptyCartResponse, {
    'empty cart lookup: status 200': (response) => response.status === 200,
    'empty cart lookup: itemCount 0': (response) => (tryJson(response, 'itemCount', -1) || 0) === 0,
  });

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
