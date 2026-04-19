import http from 'k6/http';
import { check, sleep } from 'k6';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
import { cleanupScenario, prepareScenario } from './diagnostics.js';
import {
  FORM_PARAMS,
  customerName,
  extractAntiForgeryToken,
  seededCategory,
  seededProductId,
  sessionId,
  useCartSession,
} from './shared.js';

// Page-rendering flow: browse → search → detail → cart → checkout → orders.
export const options = {
  stages: [
    { duration: '15s', target: 10 },
    { duration: '30s', target: 40 },
    { duration: '30s', target: 80 },
    { duration: '30s', target: 140 },
    { duration: '15s', target: 0 },
  ],
  thresholds: {
    http_req_duration: ['p(95)<2500'],
    http_req_failed: ['rate<0.05'],
  },
};

export function setup() {
  return prepareScenario('stress-ui');
}

export default function (context) {
  const category = seededCategory(context, 1);
  const primaryProductId = seededProductId(context, 2);
  const secondaryProductId = seededProductId(context, 3);
  const uiSession = sessionId(context, 'ui');
  const uiCustomer = customerName(context, 'ui');
  useCartSession(context, uiSession);

  const homeResponse = http.get(`${context.baseUrl}/`);
  check(homeResponse, {
    'home page: status 200': (response) => response.status === 200,
  });

  const categoryPageResponse = http.get(
    `${context.baseUrl}/Products?category=${encodeURIComponent(category)}&page=1`,
  );
  check(categoryPageResponse, {
    'category page: status 200': (response) => response.status === 200,
  });

  const searchPageResponse = http.get(
    `${context.baseUrl}/Products?q=${encodeURIComponent('Product 00')}&page=2`,
  );
  check(searchPageResponse, {
    'search page: status 200': (response) => response.status === 200,
  });

  const firstDetailResponse = http.get(`${context.baseUrl}/Products/Detail/${primaryProductId}`);
  check(firstDetailResponse, {
    'first detail page: status 200': (response) => response.status === 200,
  });
  const firstToken = extractAntiForgeryToken(firstDetailResponse);
  const firstAddResponse = http.post(
    `${context.baseUrl}/Products/Detail/${primaryProductId}`,
    {
      productId: String(primaryProductId),
      quantity: '1',
      __RequestVerificationToken: firstToken,
    },
    FORM_PARAMS,
  );
  check(firstAddResponse, {
    'add first ui item: status 200': (response) => response.status === 200,
  });

  const secondDetailResponse = http.get(`${context.baseUrl}/Products/Detail/${secondaryProductId}`);
  check(secondDetailResponse, {
    'second detail page: status 200': (response) => response.status === 200,
  });
  const secondToken = extractAntiForgeryToken(secondDetailResponse);
  const secondAddResponse = http.post(
    `${context.baseUrl}/Products/Detail/${secondaryProductId}`,
    {
      productId: String(secondaryProductId),
      quantity: '2',
      __RequestVerificationToken: secondToken,
    },
    FORM_PARAMS,
  );
  check(secondAddResponse, {
    'add second ui item: status 200': (response) => response.status === 200,
  });

  const cartPageResponse = http.get(`${context.baseUrl}/Cart`);
  check(cartPageResponse, {
    'cart page: status 200': (response) => response.status === 200,
    'cart page: shopping cart title': (response) => response.body.includes('Shopping Cart'),
  });

  const checkoutPageResponse = http.get(`${context.baseUrl}/Checkout`);
  check(checkoutPageResponse, {
    'checkout page: status 200': (response) => response.status === 200,
  });
  const checkoutToken = extractAntiForgeryToken(checkoutPageResponse);

  const checkoutSubmitResponse = http.post(
    `${context.baseUrl}/Checkout`,
    {
      customerName: uiCustomer,
      __RequestVerificationToken: checkoutToken,
    },
    FORM_PARAMS,
  );
  check(checkoutSubmitResponse, {
    'checkout submit: status 200': (response) => response.status === 200,
    'checkout submit: order placed': (response) => response.body.includes('Order Placed Successfully'),
  });

  const ordersPageResponse = http.get(
    `${context.baseUrl}/Orders?customer=${encodeURIComponent(uiCustomer)}`,
  );
  check(ordersPageResponse, {
    'orders page: status 200': (response) => response.status === 200,
    'orders page: includes customer': (response) => response.body.includes(uiCustomer),
  });

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
