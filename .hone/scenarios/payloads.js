import { customerName, productName, reviewComment, seededCategory } from './shared.js';

export function buildCartItemRequest(sessionId, productId, quantity = 1) {
  return { sessionId, productId, quantity };
}

export function buildReviewPayload(context, productId, rating, extra = '') {
  return {
    productId,
    customerName: customerName(context, 'reviewer', extra),
    rating,
    comment: reviewComment(context, productId, extra),
  };
}

export function buildOrderRequest(context, items, extra = 'checkout') {
  return {
    customerName: customerName(context, extra),
    items,
  };
}

export function buildProductPayload(context, salt = 1) {
  return {
    name: productName(context, 'product', `s${salt}`),
    description: `${context.scopePrefix} admin catalog product ${salt}`,
    price: Number((((salt % 25) + 1) * 7.5 + 9.99).toFixed(2)),
    category: seededCategory(context, salt),
  };
}
