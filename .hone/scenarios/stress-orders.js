import http from 'k6/http';
import { check, sleep } from 'k6';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
import { cleanupScenario, prepareScenario } from './diagnostics.js';
import {
  FORM_PARAMS,
  JSON_PARAMS,
  customerName,
  extractAntiForgeryToken,
  seededProductId,
  sessionId,
  tryJson,
  useCartSession,
} from './shared.js';
import { buildCartItemRequest } from './payloads.js';

// Checkout/order-history lifecycle with tagged sessions and customers so
// target-owned cleanup can reset every measured run exactly.
export const options = {
  stages: [
    { duration: '15s', target: 10 },
    { duration: '30s', target: 50 },
    { duration: '30s', target: 100 },
    { duration: '30s', target: 180 },
    { duration: '15s', target: 0 },
  ],
  thresholds: {
    http_req_duration: ['p(95)<1800'],
    http_req_failed: ['rate<0.03'],
  },
};

export function setup() {
  return prepareScenario('stress-orders');
}

export default function (context) {
  const cartSession = sessionId(context, 'checkout');
  const checkoutCustomer = customerName(context, 'orders');
  const primaryProductId = seededProductId(context, 1);
  const secondaryProductId = seededProductId(context, 2);
  useCartSession(context, cartSession);

  const addPrimaryResponse = http.post(
    `${context.baseUrl}/api/cart`,
    JSON.stringify(buildCartItemRequest(cartSession, primaryProductId, 1)),
    JSON_PARAMS,
  );
  check(addPrimaryResponse, {
    'add checkout item 1: status 200 or 201': (response) => response.status === 200 || response.status === 201,
  });

  const addSecondaryResponse = http.post(
    `${context.baseUrl}/api/cart`,
    JSON.stringify(buildCartItemRequest(cartSession, secondaryProductId, 2)),
    JSON_PARAMS,
  );
  check(addSecondaryResponse, {
    'add checkout item 2: status 200 or 201': (response) => response.status === 200 || response.status === 201,
  });

  const cartPageResponse = http.get(`${context.baseUrl}/Cart`);
  check(cartPageResponse, {
    'cart page: status 200': (response) => response.status === 200,
  });

  const checkoutPageResponse = http.get(`${context.baseUrl}/Checkout`);
  check(checkoutPageResponse, {
    'checkout page: status 200': (response) => response.status === 200,
  });
  const checkoutToken = extractAntiForgeryToken(checkoutPageResponse);

  const checkoutSubmitResponse = http.post(
    `${context.baseUrl}/Checkout`,
    {
      customerName: checkoutCustomer,
      __RequestVerificationToken: checkoutToken,
    },
    FORM_PARAMS,
  );
  check(checkoutSubmitResponse, {
    'checkout submit: status 200': (response) => response.status === 200,
    'checkout submit: order placed': (response) => response.body.includes('Order Placed Successfully'),
  });

  const ordersByCustomerResponse = http.get(
    `${context.baseUrl}/api/orders/by-customer/${encodeURIComponent(checkoutCustomer)}`,
  );
  const orders = tryJson(ordersByCustomerResponse, null, []);
  check(ordersByCustomerResponse, {
    'orders by customer: status 200': (response) => response.status === 200,
    'orders by customer: has order': () => Array.isArray(orders) && orders.length > 0,
  });

  const orderId = Array.isArray(orders) && orders.length > 0 ? orders[0].id : null;
  if (orderId) {
    const orderResponse = http.get(`${context.baseUrl}/api/orders/${orderId}`);
    check(orderResponse, {
      'order by id: status 200': (response) => response.status === 200,
    });

    const shippedResponse = http.put(
      `${context.baseUrl}/api/orders/${orderId}/status`,
      JSON.stringify({ status: 'Shipped' }),
      JSON_PARAMS,
    );
    check(shippedResponse, {
      'mark order shipped: status 204': (response) => response.status === 204,
    });

    const deliveredResponse = http.put(
      `${context.baseUrl}/api/orders/${orderId}/status`,
      JSON.stringify({ status: 'Delivered' }),
      JSON_PARAMS,
    );
    check(deliveredResponse, {
      'mark order delivered: status 204': (response) => response.status === 204,
    });
  }

  const ordersPageResponse = http.get(
    `${context.baseUrl}/Orders?customer=${encodeURIComponent(checkoutCustomer)}`,
  );
  check(ordersPageResponse, {
    'orders page: status 200': (response) => response.status === 200,
    'orders page: includes customer': (response) => response.body.includes(checkoutCustomer),
  });

  const postCheckoutCartResponse = http.get(`${context.baseUrl}/api/cart/${cartSession}`);
  check(postCheckoutCartResponse, {
    'post-checkout cart empty: status 200': (response) => response.status === 200,
    'post-checkout cart empty: itemCount 0': (response) => (tryJson(response, 'itemCount', -1) || 0) === 0,
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
