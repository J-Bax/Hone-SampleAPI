import http from 'k6/http';
import { check, sleep } from 'k6';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';
import { cleanupScenario, prepareScenario } from './diagnostics.js';
import {
  FORM_PARAMS,
  JSON_PARAMS,
  customerName,
  extractAntiForgeryToken,
  seededCategory,
  seededProductId,
  sessionId,
  tryJson,
  useCartSession,
} from './shared.js';
import { buildCartItemRequest, buildReviewPayload } from './payloads.js';

// Baseline is now a steadier mixed storefront journey. setup()/teardown() call
// target-owned diagnostics so every measured run starts and ends with a clean,
// run-scoped set of reviews, carts, checkout orders, and any admin artifacts.
export const options = {
  stages: [
    { duration: '15s', target: 8 },
    { duration: '45s', target: 24 },
    { duration: '30s', target: 36 },
    { duration: '20s', target: 18 },
    { duration: '10s', target: 0 },
  ],
  thresholds: {
    http_req_duration: ['p(95)<1500'],
    http_req_failed: ['rate<0.03'],
  },
};

export function setup() {
  return prepareScenario('baseline');
}

export default function (context) {
  const category = seededCategory(context, 1);
  const productId = seededProductId(context, 2);
  const secondaryProductId = seededProductId(context, 3);
  const cartSession = sessionId(context, 'cart');
  const checkoutCustomer = customerName(context, 'checkout');
  const reviewRating = ((__VU + __ITER) % 5) + 1;
  useCartSession(context, cartSession);

  const homeResponse = http.get(`${context.baseUrl}/`);
  check(homeResponse, {
    'home: status 200': (response) => response.status === 200,
  });
  sleep(0.3);

  const productsPageResponse = http.get(`${context.baseUrl}/Products?page=1`);
  check(productsPageResponse, {
    'products page: status 200': (response) => response.status === 200,
  });
  sleep(0.3);

  const searchResponse = http.get(`${context.baseUrl}/api/products/search?q=${encodeURIComponent('Product 00')}`);
  check(searchResponse, {
    'product search: status 200': (response) => response.status === 200,
    'product search: has matches': (response) => (tryJson(response, null, []).length || 0) > 0,
  });
  sleep(0.2);

  const categoryPageResponse = http.get(`${context.baseUrl}/Products?category=${encodeURIComponent(category)}&page=1`);
  check(categoryPageResponse, {
    'category page: status 200': (response) => response.status === 200,
  });

  const categoryApiResponse = http.get(`${context.baseUrl}/api/products/by-category/${encodeURIComponent(category)}`);
  check(categoryApiResponse, {
    'category api: status 200': (response) => response.status === 200,
  });
  sleep(0.2);

  const detailPageResponse = http.get(`${context.baseUrl}/Products/Detail/${productId}`);
  check(detailPageResponse, {
    'detail page: status 200': (response) => response.status === 200,
  });
  sleep(0.2);

  const createReviewResponse = http.post(
    `${context.baseUrl}/api/reviews`,
    JSON.stringify(buildReviewPayload(context, productId, reviewRating, 'baseline')),
    JSON_PARAMS,
  );
  check(createReviewResponse, {
    'create review: status 201': (response) => response.status === 201,
  });
  const reviewId = tryJson(createReviewResponse, 'id', null);
  sleep(0.2);

  const reviewsResponse = http.get(`${context.baseUrl}/api/reviews/by-product/${productId}`);
  check(reviewsResponse, {
    'reviews by product: status 200': (response) => response.status === 200,
  });

  const averageResponse = http.get(`${context.baseUrl}/api/reviews/average/${productId}`);
  check(averageResponse, {
    'average rating: status 200': (response) => response.status === 200,
  });

  if (reviewId) {
    const deleteReviewResponse = http.del(`${context.baseUrl}/api/reviews/${reviewId}`);
    check(deleteReviewResponse, {
      'delete review: status 204': (response) => response.status === 204,
    });
  }
  sleep(0.2);

  const addPrimaryResponse = http.post(
    `${context.baseUrl}/api/cart`,
    JSON.stringify(buildCartItemRequest(cartSession, productId, 1)),
    JSON_PARAMS,
  );
  check(addPrimaryResponse, {
    'add primary cart item: status 200 or 201': (response) => response.status === 200 || response.status === 201,
  });
  const primaryCartItemId = tryJson(addPrimaryResponse, 'id', null);

  const addSecondaryResponse = http.post(
    `${context.baseUrl}/api/cart`,
    JSON.stringify(buildCartItemRequest(cartSession, secondaryProductId, 2)),
    JSON_PARAMS,
  );
  check(addSecondaryResponse, {
    'add secondary cart item: status 200 or 201': (response) => response.status === 200 || response.status === 201,
  });
  const secondaryCartItemId = tryJson(addSecondaryResponse, 'id', null);

  const cartResponse = http.get(`${context.baseUrl}/api/cart/${cartSession}`);
  check(cartResponse, {
    'cart api: status 200': (response) => response.status === 200,
    'cart api: has items': (response) => (tryJson(response, 'itemCount', 0) || 0) >= 2,
  });

  if (primaryCartItemId) {
    const updateCartResponse = http.put(
      `${context.baseUrl}/api/cart/${primaryCartItemId}`,
      JSON.stringify(3),
      JSON_PARAMS,
    );
    check(updateCartResponse, {
      'update cart quantity: status 204': (response) => response.status === 204,
    });
  }

  if (secondaryCartItemId) {
    const removeCartResponse = http.del(`${context.baseUrl}/api/cart/${secondaryCartItemId}`);
    check(removeCartResponse, {
      'remove cart item: status 204': (response) => response.status === 204,
    });
  }
  sleep(0.3);

  const cartPageResponse = http.get(`${context.baseUrl}/Cart`);
  check(cartPageResponse, {
    'cart page: status 200': (response) => response.status === 200,
    'cart page: shows cart': (response) => response.body.includes('Shopping Cart'),
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
  sleep(0.3);

  const ordersApiResponse = http.get(
    `${context.baseUrl}/api/orders/by-customer/${encodeURIComponent(checkoutCustomer)}`,
  );
  const orders = tryJson(ordersApiResponse, null, []);
  check(ordersApiResponse, {
    'orders api: status 200': (response) => response.status === 200,
    'orders api: has order': () => Array.isArray(orders) && orders.length > 0,
  });

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

  sleep(0.5);
}

export function teardown(context) {
  cleanupScenario(context);
}

export function handleSummary(data) {
  return {
    stdout: textSummary(data, { indent: '  ', enableColors: true }),
  };
}
