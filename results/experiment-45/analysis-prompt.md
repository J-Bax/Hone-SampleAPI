Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 45)
- p95 Latency: 505.0618ms
- Requests/sec: 1244.7
- Error rate: 0%
- Improvement vs baseline: 93.3%

## Baseline Performance
- p95 Latency: 7546.103045ms
- Requests/sec: 125.5
- Error rate: 0%

## Runtime Counters
- CPU avg: 13.18%
- GC heap max: 796MB
- Gen2 collections: 32387920
- Thread pool max threads: 32

## Traffic Distribution (k6 Scenario)
The following k6 load test scenario defines the request patterns and relative weights of each
endpoint. Use this to estimate what percentage of total traffic each endpoint/code path receives.

```javascript
import http from 'k6/http';
import { check } from 'k6';

// Baseline scenario: high-concurrency stress test exercising every endpoint.
// Same user-journey shape as a real marketplace session (browse → review →
// cart → order → pages → checkout) but with zero think-time so VUs fire
// requests back-to-back, creating real server contention.
// Includes both JSON API calls and Razor Page rendering (GET + form POSTs).
// Warmup (JIT, DB, connection pool) is handled externally by warmup.js.
export const options = {
  stages: [
    { duration: '15s', target: 10 },   // Warm-up
    { duration: '30s', target: 100 },   // Normal load
    { duration: '30s', target: 300 },   // High load
    { duration: '30s', target: 500 },   // Stress load
    { duration: '15s', target: 0 },     // Cool-down
  ],
  thresholds: {
    http_req_duration: ['p(95)<2000'],  // p95 under 2s (stress test)
    http_req_failed: ['rate<0.05'],      // error rate under 5%
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';

// Extract ASP.NET anti-forgery token from a Razor Page HTML response.
function extractAntiForgeryToken(response) {
  const match = response.body.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/);
  return match ? match[1] : '';
}

// Deterministic ID generation for reproducible traffic patterns.
// Same VU + iteration + salt always produces the same ID across runs.
function seededId(max, salt) {
  const h = ((__VU * 997 + __ITER * 8191 + salt * 127) * 2654435761) >>> 0;
  return (h % max) + 1;
}

export default function () {
  const randomId = seededId(100, 1);
  const sessionId = `k6-session-${__VU}-${__ITER}`;

  // ── Product endpoints ──

  const listRes = http.get(`${BASE_URL}/api/products`);
  check(listRes, {
    'list products: status 200': (r) => r.status === 200,
    'list products: has data': (r) => {
      const body = r.json();
      return Array.isArray(body) && body.length > 0;
    },
  });

  const getRes = http.get(`${BASE_URL}/api/products/${randomId}`);
  check(getRes, {
    'get product: status 200 or 404': (r) => r.status === 200 || r.status === 404,
  });

  const searchRes = http.get(`${BASE_URL}/api/products/search?q=Product`);
  check(searchRes, {
    'search: status 200': (r) => r.status === 200,
  });

  const categoryRes = http.get(`${BASE_URL}/api/products/by-category/Electronics`);
  check(categoryRes, {
    'category filter: status 200': (r) => r.status === 200,
  });

  // ── Review endpoints ──

  const reviewProductId = seededId(500, 2);
  const reviewsRes = http.get(`${BASE_URL}/api/reviews/by-product/${reviewProductId}`);
  check(reviewsRes, {
    'reviews by product: status 200': (r) => r.status === 200,
  });

  const avgRes = http.get(`${BASE_URL}/api/reviews/average/${reviewProductId}`);
  check(avgRes, {
    'average rating: status 200': (r) => r.status === 200,
  });

  // ── Cart flow ──

  const addCartRes = http.post(`${BASE_URL}/api/cart`,
    JSON.stringify({ sessionId, productId: randomId, quantity: 1 }),
    { headers: { 'Content-Type': 'application/json' } }
  );
  check(addCartRes, {
    'add to cart: status 200 or 201': (r) => r.status === 200 || r.status === 201,
  });

  const cartRes = http.get(`${BASE_URL}/api/cart/${sessionId}`);
  check(cartRes, {
    'get cart: status 200': (r) => r.status === 200,
  });

  http.del(`${BASE_URL}/api/cart/session/${sessionId}`);

  // ── Order flow ──

  const orderRes = http.post(`${BASE_URL}/api/orders`,
    JSON.stringify({
      customerName: `k6-user-${__VU}`,
      items: [
        { productId: seededId(100, 3), quantity: 1 },
        { productId: seededId(100, 4), quantity: 2 },
      ],
    }),
    { headers: { 'Content-Type': 'application/json' } }
  );
  check(orderRes, {
    'create order: status 201': (r) => r.status === 201,
  });

  // ── Razor Pages (browsing) ──

  const homeRes = http.get(`${BASE_URL}/`);
  check(homeRes, {
    'home page: status 200': (r) => r.status === 200,
  });

  const productsPageRes = http.get(`${BASE_URL}/Products`);
  check(productsPageRes, {
    'products page: status 200': (r) => r.status === 200,
  });

  const detailPageRes = http.get(`${BASE_URL}/Products/Detail/${randomId}`);
  check(detailPageRes, {
    'product detail page: status 200': (r) => r.status === 200,
  });

  // ── Razor Pages (transactional: cart → checkout → orders) ──
  // Exercises server-side rendering with heavy DB operations:
  // N+1 queries in LoadCart/LoadCartSummary, multiple SaveChangesAsync in checkout.

  // Add to cart via the product detail page form (sets CartSessionId cookie)
  const cartProductId = seededId(100, 5);
  const addToCartToken = extractAntiForgeryToken(detailPageRes);
  const addToCartPageRes = http.post(
    `${BASE_URL}/Products/Detail/${cartProductId}`,
    { productId: String(cartProductId), quantity: '1', __RequestVerificationToken: addToCartToken }
  );
  check(addToCartPageRes, {
    'add to cart (page): status 200': (r) => r.status === 200,
  });

  // View cart page (N+1 product lookups in LoadCart)
  const cartPageRes = http.get(`${BASE_URL}/Cart`);
  check(cartPageRes, {
    'cart page: status 200': (r) => r.status === 200,
  });

  // View checkout page (same N+1 in LoadCartSummary)
  const checkoutPageRes = http.get(`${BASE_URL}/Checkout`);
  check(checkoutPageRes, {
    'checkout page: status 200': (r) => r.status === 200,
  });

  // Submit order via checkout form (heaviest operation: N+1 + per-item SaveChanges)
  const customerName = `k6-checkout-${__VU}`;
  const checkoutToken = extractAntiForgeryToken(checkoutPageRes);
  const checkoutSubmitRes = http.post(
    `${BASE_URL}/Checkout`,
    { customerName: customerName, __RequestVerificationToken: checkoutToken }
  );
  check(checkoutSubmitRes, {
    'checkout submit: status 200': (r) => r.status === 200,
  });

  // View order history page (N+1 product name lookups)
  const ordersPageRes = http.get(
    `${BASE_URL}/Orders?customer=${encodeURIComponent(customerName)}`
  );
  check(ordersPageRes, {
    'orders page: status 200': (r) => r.status === 200,
  });
}

export function handleSummary(data) {
  return {
    stdout: textSummary(data, { indent: '  ', enableColors: true }),
  };
}

// k6 built-in text summary helper
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';

```

## Previously Tried Optimizations
# Optimization Log

> Auto-generated by Hone. Each entry records an optimization that was proposed and its outcome.

| Experiment | Timestamp | File | Optimization | Outcome |
|-----------|-----------|------|-------------|---------|
| 1 | 2026-03-15 12:02 | `SampleApi/Controllers/ProductsController.cs` | Eliminate full-table product scans with server-side filtering | improved |
| 2 | 2026-03-15 12:28 | `SampleApi/Controllers/ReviewsController.cs` | Replace full reviews table scan with server-side query filtering | improved |
| 3 | 2026-03-15 12:54 | `SampleApi/Pages/Checkout/Index.cshtml.cs` | Eliminate N+1 queries and per-item SaveChanges in checkout flow | improved |
| 4 | 2026-03-15 13:30 | `SampleApi/Pages/Orders/Index.cshtml.cs` | Eliminate full-table scans and N+1 queries in Orders page | improved |
| 5 | 2026-03-15 13:56 | `SampleApi/Controllers/CartController.cs` | Replace full CartItems table scans with server-side filtering | improved |
| 6 | 2026-03-15 15:18 | `SampleApi/Pages/Products/Detail.cshtml.cs` | Eliminate full Reviews and Products table scans in product detail page | improved |
| 7 | 2026-03-15 15:54 | `SampleApi/Pages/Cart/Index.cshtml.cs` | Eliminate full CartItems table scan and N+1 product lookups in Cart page LoadCart | improved |
| 8 | 2026-03-15 16:19 | `SampleApi/Pages/Index.cshtml.cs` | Replace dual full-table scans with targeted queries on Home page | improved |
| 9 | 2026-03-15 16:44 | `SampleApi/Pages/Products/Index.cshtml.cs` | Replace full product table scan with server-side filtering and pagination | improved |
| 10 | 2026-03-15 17:26 | `SampleApi/Controllers/OrdersController.cs` | Batch product lookups and add AsNoTracking in CreateOrder | improved |
| 11 | 2026-03-15 17:56 | `SampleApi/Pages/Checkout/Index.cshtml.cs` | Consolidate redundant SaveChangesAsync round trips in checkout post | improved |
| 12 | 2026-03-15 18:35 | `SampleApi/Controllers/ReviewsController.cs` | Consolidate redundant DB round trips in review endpoints | improved |
| 13 | 2026-03-15 18:59 | `SampleApi/Controllers/CartController.cs` | Add AsNoTracking to read-only cart and product queries | improved |
| 14 | 2026-03-15 19:24 | `SampleApi/Controllers/OrdersController.cs` | Replace full table scans and N+1 queries in order read endpoints | stale |
| 15 | 2026-03-15 20:04 | `SampleApi/Pages/Products/Detail.cshtml.cs` | Add AsNoTracking to all read-only queries on Detail page | improved |
| 16 | 2026-03-15 20:52 | `SampleApi/Data/AppDbContext.cs` | Add database indexes on high-traffic filter columns | improved |
| 17 | 2026-03-15 21:17 | `SampleApi/Pages/Checkout/Index.cshtml.cs` | Add AsNoTracking and Select projections to Checkout read queries | improved |
| 18 | 2026-03-15 21:40 | `SampleApi/Controllers/ProductsController.cs` | Test failure: Add pagination and DTO projection to product list endpoints | regressed |
| 19 | 2026-03-15 22:05 | `SampleApi/Controllers/ReviewsController.cs` | Eliminate redundant product existence DB round trips in review endpoints | improved |
| 20 | 2026-03-15 22:30 | `SampleApi/Controllers/OrdersController.cs` | Add AsNoTracking and server-side filtering to GetOrder with batched product lookup | improved |
| 21 | 2026-03-15 23:06 | `SampleApi/Controllers/ProductsController.cs` | Eliminate redundant category existence DB round trip in GetProductsByCategory | improved |
| 22 | 2026-03-15 23:31 | `SampleApi/Pages/Cart/Index.cshtml.cs` | Add Select projection to product lookup in Cart page LoadCart | improved |
| 23 | 2026-03-15 23:55 | `SampleApi/Pages/Orders/Index.cshtml.cs` | Add Select projection to product name lookup in Orders page | improved |
| 24 | 2026-03-16 00:09 | `SampleApi/Program.cs` | Test failure: Replace AddDbContext with AddDbContextPool to reduce allocation pressure | regressed |
| 25 | 2026-03-16 00:33 | `SampleApi/Pages/Index.cshtml.cs` | Replace NEWID() random ordering with efficient deterministic query for featured products | stale |
| 26 | 2026-03-16 08:59 | `SampleApi/Controllers/CartController.cs` | Add Select projection to product dictionary lookup in GetCart API endpoint | improved |
| 27 | 2026-03-16 09:45 | `SampleApi/Controllers/ProductsController.cs` | Add Select projection excluding Description on product list endpoints and AsNoTracking on GetProduct | improved |
| 28 | 2026-03-16 10:09 | `SampleApi/Controllers/CartController.cs` | Replace FindAsync with AnyAsync for product existence check in AddToCart | stale |
| 29 | 2026-03-16 10:34 | `SampleApi/Controllers/OrdersController.cs` | Add Select projection to product lookup in CreateOrder | improved |
| 30 | 2026-03-16 10:50 | `SampleApi/Pages/Products/Index.cshtml.cs` | Build failure: Add Select projection to paginated product query excluding Description | regressed |
| 31 | 2026-03-16 11:15 | `SampleApi/Controllers/CartController.cs` | Replace materialize-then-remove with raw SQL DELETE in ClearCart | improved |
| 32 | 2026-03-16 11:39 | `SampleApi/Pages/Index.cshtml.cs` | Add Select projection to FeaturedProducts and RecentReviews excluding large text fields | improved |
| 33 | 2026-03-16 12:17 | `SampleApi/Pages/Products/Detail.cshtml.cs` | Add Select projections to Reviews and RelatedProducts queries on Detail page | improved |
| 34 | 2026-03-16 12:41 | `SampleApi/Pages/Products/Index.cshtml.cs` | Add Select projection to paginated product query excluding Description | improved |
| 35 | 2026-03-16 13:06 | `SampleApi/Program.cs` | Set minimum log level to Warning to reduce per-request logging overhead | improved |
| 36 | 2026-03-16 20:15 | `SampleApi/Controllers/ProductsController.cs` | Test failure: Add result limit to search endpoint returning all 1000 products | regressed |
| 37 | 2026-03-16 20:39 | `SampleApi/Pages/Index.cshtml.cs` | Replace ORDER BY NEWID() with efficient Skip/Take random sampling | improved |
| 38 | 2026-03-16 21:04 | `SampleApi/Pages/Checkout/Index.cshtml.cs` | Use AsNoTracking and raw SQL DELETE for cart cleanup in checkout | improved |
| 39 | 2026-03-16 21:54 | `SampleApi/Controllers/OrdersController.cs` | Consolidate two SaveChangesAsync into one in CreateOrder | improved |
| 40 | 2026-03-16 22:19 | `SampleApi/Pages/Checkout/Index.cshtml.cs` | Consolidate two SaveChangesAsync into one in checkout OnPostAsync | improved |
| 41 | 2026-03-16 22:43 | `SampleApi/Controllers/ReviewsController.cs` | Add Select projection to GetReviewsByProduct excluding Comment | improved |
| 42 | 2026-03-16 23:23 | `SampleApi/Program.cs` | Configure JSON to skip null property serialization | stale |
| 43 | 2026-03-16 23:48 | `SampleApi/Controllers/ProductsController.cs` | Add Select projection to GetProduct single-entity endpoint | stale |
| 44 | 2026-03-17 00:12 | `SampleApi/Controllers/CartController.cs` | Combine two-query GetCart into single join query | improved |


## Known Optimization Queue
- [TRIED] `SampleApi/Program.cs` — Configure JSON to skip null property serialization *(experiment 42 — stale)*
- [TRIED] `SampleApi/Controllers/ProductsController.cs` — Add Select projection to GetProduct single-entity endpoint *(experiment 43 — stale)*
- [TRIED] `SampleApi/Controllers/CartController.cs` — Combine two-query GetCart into single join query *(experiment 44 — improved)*

## Last Experiment's Fix
Combine two-query GetCart into single join query

## Experiment History (with metrics)
Do NOT re-attempt optimizations that were already tried and resulted in stale or regressed outcomes. Propose different targets or approaches instead.
| Exp | File | Outcome | p95 (ms) | RPS | Branch |
|-----|------|---------|----------|-----|--------|
| 1 | — | improved | 2203.8 | 341.5 | hone/experiment-1 |
| 2 | — | improved | 2166.1 | 346.6 | hone/experiment-2 |
| 3 | — | improved | 2179.4 | 349.3 | hone/experiment-3 |
| 4 | — | improved | 793 | 773.4 | hone/experiment-4 |
| 5 | — | improved | 780.9 | 764.5 | hone/experiment-5 |
| 6 | — | improved | 685 | 884.3 | hone/experiment-6 |
| 7 | — | improved | 677.4 | 872.1 | hone/experiment-7 |
| 8 | — | improved | 606.9 | 976.2 | hone/experiment-8 |
| 9 | — | improved | 591.9 | 980.9 | hone/experiment-9 |
| 10 | — | improved | 540.5 | 1033.1 | hone/experiment-10 |
| 11 | — | improved | 548.4 | 1006.2 | hone/experiment-11 |
| 12 | — | improved | 546.3 | 1028.9 | hone/experiment-12 |
| 13 | — | improved | 548.8 | 1032.4 | hone/experiment-13 |
| 14 | — | stale | 551.6 | 1025.2 | hone/experiment-14 |
| 15 | — | improved | 546 | 1025.7 | hone/experiment-15 |
| 16 | — | improved | 595.7 | 698.5 | hone/experiment-16 |
| 17 | — | improved | 536 | 1052.1 | hone/experiment-17 |
| 18 | — | test_failure | N/A | N/A | hone/experiment-18 |
| 19 | — | improved | 537.1 | 1055.8 | hone/experiment-19 |
| 20 | — | improved | 544.8 | 1078.1 | hone/experiment-20 |
| 21 | — | improved | 535.2 | 1093.7 | hone/experiment-21 |
| 22 | — | improved | 535.1 | 1091.5 | hone/experiment-22 |
| 23 | — | improved | 546.1 | 1100.1 | hone/experiment-23 |
| 24 | — | test_failure | N/A | N/A | hone/experiment-24 |
| 25 | — | stale | 548.4 | 1060.2 | hone/experiment-25 |
| 26 | — | improved | 544.7 | 1087.3 | hone/experiment-26 |
| 27 | — | improved | 535.2 | 1123.3 | hone/experiment-27 |
| 28 | — | stale | 541.6 | 1090.9 | hone/experiment-28 |
| 29 | — | improved | 534.5 | 1101.5 | hone/experiment-29 |
| 30 | — | build_failure | N/A | N/A | hone/experiment-30 |
| 31 | — | improved | 545.1 | 1127.8 | hone/experiment-31 |
| 32 | — | improved | 537.7 | 1124.5 | hone/experiment-32 |
| 33 | — | improved | 543.8 | 1140.3 | hone/experiment-33 |
| 34 | — | improved | 535.4 | 1122.1 | hone/experiment-34 |
| 35 | — | improved | 544.4 | 1129.2 | hone/experiment-35 |
| 36 | — | test_failure | N/A | N/A | hone/experiment-36 |
| 37 | — | improved | 517.3 | 1155.3 | hone/experiment-37 |
| 38 | — | improved | 513.9 | 1166.3 | hone/experiment-38 |
| 39 | — | improved | 512.6 | 1170.5 | hone/experiment-39 |
| 40 | — | improved | 499 | 1198.6 | hone/experiment-40 |
| 41 | — | improved | 497.1 | 1231 | hone/experiment-41 |
| 42 | — | stale | 498.7 | 1203.2 | hone/experiment-42 |
| 43 | — | stale | 501.7 | 1196.8 | hone/experiment-43 |
| 44 | — | improved | 505.1 | 1244.7 | hone/experiment-44 |


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.SqlDataReader.TryReadColumnInternal","inclusivePct":8.2,"exclusivePct":0.69,"callChain":["EF Core ToListAsync","SingleQueryingEnumerable.MoveNextAsync","RelationalCommand.ExecuteReaderAsync","SqlDataReader.ReadAsync","TryReadInternal","TryReadColumnInternal"],"observation":"Top SQL data-reading method with 1653 exclusive samples. The combined SQL client stack (TDS parser, column reading, string decoding) totals ~12K samples — queries return large result sets with many columns, indicating missing projection (Select) or pagination (Skip/Take)."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1+AsyncEnumerator.MoveNextAsync","inclusivePct":10.5,"exclusivePct":0.35,"callChain":["ToListAsync","ConfiguredCancelableAsyncEnumerable.MoveNextAsync","SingleQueryingEnumerable.MoveNextAsync","→ entire SqlDataReader pipeline"],"observation":"EF Core row enumeration driving the entire SQL reading pipeline. Combined with ToListAsync (315 samples), this indicates full collection materialization — loading all rows into memory without server-side pagination or filtering, causing cascading CPU cost through SQL parsing, Unicode decoding, and JSON serialization."},{"method":"System.Text.Json.Serialization.Converters.ObjectDefaultConverter`1.OnTryWrite","inclusivePct":3.8,"exclusivePct":0.46,"callChain":["JsonSerializer.SerializeAsync","JsonConverter.TryWrite","ObjectDefaultConverter.OnTryWrite","→ JsonPropertyInfo.GetMemberAndWriteJson","→ Utf8JsonWriter methods"],"observation":"JSON serialization consumes ~5K combined samples across the write stack (OnTryWrite 1107, GetMemberAndWriteJson 507, WriteStack.Push 487, ToUtf8 435, WritePropertyNameSection 367). Large object graphs serialized per response — a downstream symptom of over-fetching from the database."},{"method":"System.Runtime.CompilerServices.CastHelpers.IsInstanceOfInterface","inclusivePct":1.03,"exclusivePct":1.03,"callChain":["EF Core materialization / DI resolution / MVC pipeline","→ CastHelpers.IsInstanceOfInterface"],"observation":"Interface type-checking at 2460 exclusive samples. Combined with IsInstanceOfClass (2186), ChkCastAny (690), IsInstanceOfAny (636), StelemRef (907+543) — totaling ~8K samples on type casting. Driven by high-volume EF entity materialization and DI polymorphic dispatch. Reducing rows materialized directly reduces this cost."},{"method":"Microsoft.Extensions.DependencyInjection.dynamicClass.ResolveService","inclusivePct":2.8,"exclusivePct":0.39,"callChain":["Controller/middleware","ServiceProvider.GetService","ResolveService","→ Dictionary.FindValue/TryInsert (ServiceCacheKey)"],"observation":"DI resolution at 938 exclusive samples plus service-cache Dictionary lookups (867) and inserts (309). Suggests DbContext or repositories resolved multiple times per request. Consider reducing scoped service resolution churn or caching resolved instances within the request scope."},{"method":"System.Text.UnicodeEncoding.GetCharCount","inclusivePct":1.21,"exclusivePct":0.76,"callChain":["SqlDataReader.TryReadColumnInternal","TdsParser.TryReadSqlStringValue","UnicodeEncoding.GetCharCount / GetChars","→ String.CreateStringFromEncoding"],"observation":"Unicode decoding at 1817 + 1098 samples (GetCharCount + GetChars) plus String.CreateStringFromEncoding (311). This is the direct CPU cost of decoding string columns from the TDS wire protocol. Fetching fewer string columns via projection (Select) would reduce this proportionally."},{"method":"System.Reflection.DefaultBinder.SelectMethod","inclusivePct":1.2,"exclusivePct":0.3,"callChain":["RuntimeType.GetMethodImplCommon","DefaultBinder.SelectMethod","→ FilterApplyMethodBase","→ CerHashtable.get_Item"],"observation":"Reflection-based method resolution at 725 + 436 + 282 exclusive samples, with CerHashtable lookups (524) and GetParametersCached (260). Likely from LINQ expression compilation or model binding under load. Consider pre-compiled queries (EF compiled queries) to eliminate repeated reflection."},{"method":"System.Threading.SemaphoreSlim.Wait","inclusivePct":0.25,"exclusivePct":0.13,"callChain":["Possible sync-over-async call path","SemaphoreSlim.Wait","→ SpinWait.SpinOnceCore"],"observation":"Synchronous SemaphoreSlim.Wait (319 samples) plus SpinWait.SpinOnceCore (281) indicates blocking waits on the thread pool — a sync-over-async anti-pattern. Under 1244 req/s this wastes threads and increases tail latency. Audit all code paths for .Result or .Wait() calls on Tasks."},{"method":"Microsoft.AspNetCore.Mvc.ViewFeatures.Buffers.ViewBuffer.WriteToAsync","inclusivePct":0.45,"exclusivePct":0.12,"callChain":["MVC pipeline","ViewResultExecutor","ViewBuffer.WriteToAsync","→ StreamWriter.Flush / StringWriter.Write"],"observation":"MVC Razor view rendering (299 samples) plus AnsiParser.Parse (241) and StreamWriter.Flush (329) is unexpected for a Web API. If any endpoints return Razor views instead of JSON, converting them to JSON responses would eliminate this overhead. Console logging (AnsiParser) adds measurable cost under load."}],"summary":"The CPU profile is dominated by SQL data reading (~12K samples across the SqlClient/TDS stack) and JSON serialization (~5K samples), strongly indicating that endpoints fetch entire database collections without pagination or column projection, then serialize oversized object graphs. The single most impactful optimization is adding server-side pagination (Skip/Take) and projection (Select only needed columns) to EF Core queries — this would simultaneously reduce SQL wire-protocol parsing, Unicode string decoding, EF materialization casting overhead, and JSON serialization cost. Secondary priorities: (1) investigate the synchronous SemaphoreSlim.Wait blocking pattern that could degrade tail latency under load, (2) replace any Razor view rendering with direct JSON responses, and (3) consider EF compiled queries to eliminate the reflection overhead from repeated expression compilation."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.73,"gen1Rate":0.63,"gen2Rate":0.04,"pauseTimeMs":{"avg":6.2,"max":139.5,"total":1033.7},"gcPauseRatio":0.9,"fragmentationPct":0.0,"observations":["Gen1 collection count (75) is 85% of Gen0 count (88) — this is a severe mid-life crisis pattern where nearly all Gen0 survivors are promoted to Gen1 and then die there, indicating objects that live just long enough to escape Gen0 (e.g., async state machines, buffered response objects, EF change tracker entries)","Gen1 max pause of 139.5ms is extremely high and directly contributes to the 505ms p95 latency — a single Gen1 collection can stall all request threads for over 100ms","Gen1 avg pause (7.9ms) is 61% higher than Gen0 avg pause (4.9ms), confirming Gen1 heap is accumulating significant volume before collection","Gen2 collections are low (5 total, avg 2.4ms) — long-lived object management is healthy and LOH pressure is minimal","Overall GC pause ratio of 0.9% is below the 5% concern threshold, but the tail latency impact from Gen1 max pauses is the real problem — p95/p99 latency is dominated by these outlier GC events"]},"heapAnalysis":{"peakSizeMB":818.63,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 818MB is very large for an API workload at ~1245 req/s — this suggests significant object retention or large per-request working sets","Total allocation of 35,137MB over the test run implies an allocation rate of ~293 MB/s, which is extremely high and the root cause of GC pressure — at 1245 req/s this is roughly 235KB allocated per request","Zero fragmentation is positive — LOH compaction is not an issue and large object allocations are not creating holes in the managed heap"]},"topAllocators":[{"type":"(allocation type data unavailable)","allocMB":null,"pctOfTotal":null,"callSite":"unknown — no allocation tick data captured","observation":"Allocation sampling was not enabled or captured no data. Re-run PerfView with /DotNetAllocSampled and export GC Heap Alloc Stacks to identify the specific types and call sites responsible for the ~293 MB/s allocation rate. Without this data, focus on common EF Core + Web API patterns: materialized query results (ToListAsync on large result sets), string serialization buffers, and response body buffering."}],"summary":"The dominant memory issue is a textbook mid-life crisis: 85% of Gen0 collections promote objects into Gen1, where they die — driving Gen1 pauses up to 139.5ms and directly inflating p95 latency. The allocation rate of ~293 MB/s (~235KB per request) is the root cause. The #1 fix should target reducing per-request allocations: look for EF Core queries materializing large collections (use AsNoTracking, project with Select instead of loading full entities), avoid intermediate List/Array allocations in controller pipelines, and consider object pooling (ArrayPool<T>, ObjectPool<T>) for recurring buffers. Reducing the allocation volume will lower Gen0 frequency, cut Gen1 promotions, and eliminate the tail-latency spikes caused by long Gen1 pauses."}
```


## Source Files
The following source files are available for analysis (paths relative to repo root).
Read the files that are relevant to identifying performance bottlenecks.

- sample-api/SampleApi/Controllers/CartController.cs
- sample-api/SampleApi/Controllers/CategoriesController.cs
- sample-api/SampleApi/Controllers/OrdersController.cs
- sample-api/SampleApi/Controllers/ProductsController.cs
- sample-api/SampleApi/Controllers/ReviewsController.cs
- sample-api/SampleApi/Data/AppDbContext.cs
- sample-api/SampleApi/Data/SeedData.cs
- sample-api/SampleApi/Models/AddToCartRequest.cs
- sample-api/SampleApi/Models/CartItem.cs
- sample-api/SampleApi/Models/Category.cs
- sample-api/SampleApi/Models/CreateOrderRequest.cs
- sample-api/SampleApi/Models/Order.cs
- sample-api/SampleApi/Models/OrderItem.cs
- sample-api/SampleApi/Models/Product.cs
- sample-api/SampleApi/Models/Review.cs
- sample-api/SampleApi/Models/UpdateOrderStatusRequest.cs
- sample-api/SampleApi/Pages/Index.cshtml.cs
- sample-api/SampleApi/Pages/Cart/Index.cshtml.cs
- sample-api/SampleApi/Pages/Checkout/Index.cshtml.cs
- sample-api/SampleApi/Pages/Orders/Index.cshtml.cs
- sample-api/SampleApi/Pages/Products/Detail.cshtml.cs
- sample-api/SampleApi/Pages/Products/Index.cshtml.cs

Respond with JSON only. No markdown, no code blocks around the JSON.
