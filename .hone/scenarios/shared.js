import http from 'k6/http';

export const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
export const JSON_PARAMS = {
  headers: { 'Content-Type': 'application/json' },
};
export const FORM_PARAMS = { redirects: 0 };

const DEFAULT_CATEGORIES = [
  'Automotive',
  'Books',
  'Clothing',
  'Electronics',
  'Food & Beverage',
  'Health',
  'Home & Garden',
  'Office Supplies',
  'Sports',
  'Toys',
];

export function createScenarioContext(name) {
  const scenarioName = name.toLowerCase().replace(/[^a-z0-9]+/g, '-');
  const envRunId = (__ENV.HONE_RUN_ID || __ENV.HONE_SCENARIO_RUN_ID || '').trim();
  const timestamp = new Date().toISOString().replace(/[^\d]/g, '').slice(0, 14);
  const uniqueSuffix = Math.random().toString(36).slice(2, 8);
  const runId = envRunId || `${timestamp}-${uniqueSuffix}`;
  const familyPrefix = `hone-${scenarioName}`;

  return {
    scenarioName,
    runId,
    familyPrefix,
    scopePrefix: `${familyPrefix}-${runId}`,
    baseUrl: BASE_URL,
    catalog: {
      productCount: 1000,
      categoryCount: DEFAULT_CATEGORIES.length,
      categories: DEFAULT_CATEGORIES.slice(),
    },
  };
}

export function mergeCatalog(context, catalog) {
  if (!catalog) {
    return context;
  }

  const categories = Array.isArray(catalog.categories) && catalog.categories.length > 0
    ? catalog.categories
    : context.catalog.categories;

  return {
    ...context,
    catalog: {
      productCount: catalog.productCount || context.catalog.productCount,
      categoryCount: catalog.categoryCount || categories.length,
      categories,
    },
  };
}

export function seededProductId(context, salt) {
  const productCount = Math.max(1, Math.min(context.catalog.productCount || 1000, 1000));
  return deterministicIndex(productCount, salt) + 1;
}

export function seededCategory(context, salt) {
  const categories = Array.isArray(context.catalog.categories) && context.catalog.categories.length > 0
    ? context.catalog.categories
    : DEFAULT_CATEGORIES;

  return categories[deterministicIndex(categories.length, salt)];
}

export function customerName(context, resource = 'customer', extra = '') {
  return taggedValue(context, resource, 100, extra);
}

export function sessionId(context, resource = 'session', extra = '') {
  return taggedValue(context, resource, 100, extra);
}

export function productName(context, resource = 'product', extra = '') {
  return taggedValue(context, resource, 200, extra);
}

export function reviewComment(context, productId, extra = '') {
  const suffix = extra ? ` ${extra}` : '';
  return truncate(`${context.scopePrefix} review for product ${productId}${suffix}`, 2000);
}

export function useCartSession(context, value) {
  const jar = http.cookieJar();
  jar.set(context.baseUrl, 'CartSessionId', value, { path: '/' });
  return value;
}

export function extractAntiForgeryToken(response) {
  const match = response.body.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/i);
  return match ? match[1] : '';
}

export function tryJson(response, selector = null, fallback = null) {
  try {
    return selector ? response.json(selector) : response.json();
  } catch (_) {
    return fallback;
  }
}

function deterministicIndex(max, salt) {
  const safeMax = Math.max(1, max);
  const hash = ((__VU * 977 + __ITER * 7919 + salt * 131) * 2654435761) >>> 0;
  return hash % safeMax;
}

function taggedValue(context, resource, maxLength, extra = '') {
  const iterationLabel = `vu${__VU}-iter${__ITER}${extra ? `-${extra}` : ''}`;
  return truncate(`${context.scopePrefix}-${resource}-${iterationLabel}`, maxLength);
}

function truncate(value, maxLength) {
  return value.length > maxLength ? value.slice(0, maxLength) : value;
}
