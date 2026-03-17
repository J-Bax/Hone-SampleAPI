Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 42)
- p95 Latency: 497.12074ms
- Requests/sec: 1231
- Error rate: 0%
- Improvement vs baseline: 93.4%

## Baseline Performance
- p95 Latency: 7546.103045ms
- Requests/sec: 125.5
- Error rate: 0%

## Runtime Counters
- CPU avg: 13.05%
- GC heap max: 823MB
- Gen2 collections: 34080144
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


## Known Optimization Queue
- [TRIED] `SampleApi/Controllers/OrdersController.cs` — Consolidate two SaveChangesAsync into one in CreateOrder *(experiment 39 — improved)*
- [TRIED] `SampleApi/Pages/Checkout/Index.cshtml.cs` — Consolidate two SaveChangesAsync into one in checkout OnPostAsync *(experiment 40 — improved)*
- [TRIED] `SampleApi/Controllers/ReviewsController.cs` — Add Select projection to GetReviewsByProduct excluding Comment *(experiment 41 — improved)*

## Last Experiment's Fix
Add Select projection to GetReviewsByProduct excluding Comment

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


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.SqlDataReader.TryReadColumnInternal","inclusivePct":3.92,"exclusivePct":0.67,"callChain":["EFCore.SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun","SqlDataReader.TryReadColumnInternal"],"observation":"Top application-level CPU consumer. The TDS data-reading pipeline (TryReadColumnInternal, TryRun, TryGetTokenLength, TryReadSqlValue, and 10+ related methods) collectively accounts for ~21% of attributable application CPU. This volume of column parsing strongly suggests over-fetching — either SELECT * returning unneeded columns, loading too many rows per request, or both."},{"method":"System.Text.Json.Serialization.Converters.ObjectDefaultConverter`1.OnTryWrite","inclusivePct":1.96,"exclusivePct":0.44,"callChain":["Controller.Action","JsonSerializer.Serialize","ObjectDefaultConverter.OnTryWrite","JsonPropertyInfo.GetMemberAndWriteJson"],"observation":"JSON serialization accounts for ~10.7% of attributable app CPU across 11 methods (WriteNullSection, WritePropertyNameSection, WriteStringMinimized, etc.). The notable WriteNullSection (481 samples) indicates many null properties being serialized — consider [JsonIgnore(Condition = WhenWritingNull)] or DTO projection to exclude unused fields."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1+AsyncEnumerator.MoveNextAsync","inclusivePct":5.85,"exclusivePct":0.31,"callChain":["ToListAsync","SingleQueryingEnumerable.MoveNextAsync","RelationalCommand.ExecuteReaderAsync","SqlDataReader.ReadAsync"],"observation":"High inclusive % (encompasses all SQL reading + materialization below it) but low exclusive % — this is a call-site orchestrating EF Core query execution. Combined with the heavy SQL reader activity, this indicates queries return large result sets. Consider using .Select() projections, pagination, or compiled queries to reduce data volume."},{"method":"Microsoft.Extensions.DependencyInjection.ServiceProvider.ResolveService","inclusivePct":1.39,"exclusivePct":0.4,"callChain":["Kestrel.RequestProcessing","ServiceProvider.GetService","ResolveService","Dictionary.FindValue(ServiceCacheKey)"],"observation":"DI resolution consumes ~5.3% of attributable app CPU (ResolveService 947 + ServiceCacheKey dictionary lookups 782 + GetService 290 + cache inserts 312). This suggests many transient services resolved per request. Consider converting hot-path services to Singleton or Scoped lifetime, or injecting IServiceProvider less frequently."},{"method":"System.Runtime.CompilerServices.CastHelpers.IsInstanceOfInterface","inclusivePct":3.28,"exclusivePct":0.96,"callChain":["EFCore.Materialization","CastHelpers.IsInstanceOfInterface"],"observation":"Type-casting helpers collectively consume ~17.9% of attributable app CPU (IsInstanceOfInterface 2292, IsInstanceOfClass 2067, StelemRef 980+498, ChkCast 708+315, IsInstanceOfAny 754). This unusually high casting overhead is characteristic of EF Core entity materialization over large result sets and heavy DI interface resolution. Reducing rows materialized will directly reduce this."},{"method":"System.Text.UnicodeEncoding.GetCharCount","inclusivePct":1.32,"exclusivePct":0.65,"callChain":["SqlDataReader.TryReadColumnInternal","TdsParser.TryReadSqlStringValue","UnicodeEncoding.GetCharCount"],"observation":"Unicode encoding/decoding (GetCharCount 1558 + GetChars 1106 + GetString 226 + CreateStringFromEncoding 273) totals ~7.2% of attributable app CPU. This string materialization is driven by SQL string column reads — the application is reading and allocating many string values from the database. Fetching fewer string columns via projection would reduce this proportionally."},{"method":"System.Threading.SemaphoreSlim.Wait","inclusivePct":0.15,"exclusivePct":0.15,"callChain":["RequestPipeline","SemaphoreSlim.Wait"],"observation":"Synchronous SemaphoreSlim.Wait (355 samples) blocks a thread pool thread, reducing throughput under load. At 1231 RPS this is a concurrency bottleneck. This should be replaced with SemaphoreSlim.WaitAsync to free the thread while waiting. Check for any .Result or .Wait() calls in the request pipeline."},{"method":"System.DefaultBinder.SelectMethod","inclusivePct":0.69,"exclusivePct":0.28,"callChain":["RuntimeType.GetMethodImplCommon","DefaultBinder.SelectMethod","RuntimeType.FilterApplyMethodBase"],"observation":"Reflection-based method resolution (SelectMethod 671 + GetMethodImplCommon 477 + FilterApplyMethodBase 251 + GetParametersCached 260) totals ~3.8% of attributable app CPU. This likely comes from DI container or model binding using runtime reflection on hot paths. Caching resolved methods or using source-generated alternatives would help."},{"method":"Microsoft.AspNetCore.Mvc.ViewFeatures.Buffers.ViewBuffer.WriteToAsync","inclusivePct":0.13,"exclusivePct":0.13,"callChain":["MvcPipeline","ViewBuffer.WriteToAsync"],"observation":"ViewBuffer usage (307 samples) indicates the API may be rendering Razor views rather than returning raw JSON via Ok()/JsonResult. For a pure API, switching to minimal API endpoints or ensuring controllers return ObjectResult (not views) eliminates the view rendering pipeline overhead entirely."},{"method":"System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start","inclusivePct":0.96,"exclusivePct":0.32,"callChain":["RequestPipeline","AsyncMethodBuilderCore.Start","ExecutionContext.OnValuesChanged"],"observation":"Async state machine overhead (Start 769 + ExecutionContext changes 510+481+303 + Task.ScheduleAndStart 240) consumes ~5.3% of attributable app CPU. This indicates a deep async call chain with many await points. Reducing unnecessary async layers or using ValueTask for hot paths that often complete synchronously would help."}],"summary":"The CPU profile is dominated by SQL data reading (~21% of application CPU) and entity materialization, strongly suggesting the API over-fetches data — likely returning full entities with all columns instead of projected DTOs. The second major theme is serialization overhead (~11%), with notable null-property serialization indicating bloated response payloads. The developer should focus on three changes: (1) add .Select() projections to EF Core queries to fetch only needed columns, drastically reducing SQL parsing, string allocation, type casting, and Unicode decoding in one fix; (2) apply [JsonIgnore(Condition = WhenWritingNull)] or use slim DTOs to cut serialization waste; (3) eliminate the synchronous SemaphoreSlim.Wait which is a concurrency bottleneck at the current 1231 RPS load."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.73,"gen1Rate":0.58,"gen2Rate":0.03,"pauseTimeMs":{"avg":6.7,"max":119.7,"total":1099.3},"gcPauseRatio":0.9,"fragmentationPct":0.0,"observations":["Gen1 collection count (71) is abnormally close to Gen0 count (89) — an 80% survival ratio from Gen0 to Gen1 indicates a large volume of mid-lived objects that survive Gen0 but die in Gen1. Normal Gen1/Gen0 ratio is 10-20%, not 80%.","Gen1 max pause of 119.7ms is the worst pause in the entire profile and directly contributes to the 497ms p95 latency — Gen1 collections are compacting a large surviving object set.","Gen0 avg pause of 6.3ms with max of 94.5ms shows significant variance, suggesting some Gen0 collections are triggered when the heap is bloated with soon-to-be-promoted objects.","Gen2 collections are healthy (4 total, max 4.0ms pause) — long-lived objects are well-managed and LOH pressure is minimal.","GC pause ratio of 0.9% is within acceptable range (<5%), so overall GC throughput is not the bottleneck — the problem is individual pause spikes impacting tail latency."]},"heapAnalysis":{"peakSizeMB":836.4,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 836.4MB under load is substantial — combined with ~35GB total allocations over ~122s, the effective allocation rate is ~287 MB/sec, indicating extreme allocation churn.","Zero fragmentation suggests LOH is not a concern and the GC is compacting effectively, but the sheer volume of Gen0→Gen1 promotions is inflating the working set.","The ratio of total allocations (34,970 MB) to peak heap (836 MB) means objects are being allocated and collected ~42x over — this churn is the primary driver of GC activity."]},"topAllocators":[{"type":"(allocation type data not captured)","allocMB":null,"pctOfTotal":null,"callSite":"unknown","observation":"PerfView allocation tick sampling was not enabled or did not capture type-level data. Re-run with /DotNetAllocSampled and export the GC Heap Alloc Stacks view to identify which types and call sites are responsible for the ~287 MB/sec allocation rate."}],"summary":"The dominant issue is an abnormally high Gen1 collection rate — 80% of Gen0 survivors are promoted to Gen1, indicating mid-lived objects (likely per-request allocations such as large DTOs, deserialized payloads, or buffered response bodies) that outlive Gen0 but die before Gen2. This creates heavy Gen1 compaction work with pause spikes up to 119.7ms that directly inflate p95 latency. The #1 fix is to identify and eliminate mid-lived allocations: use object pooling (ArrayPool<T>, ObjectPool<T>) for buffers, enable response streaming instead of buffering, and reduce DTO sizes or cache repeated query results. Re-run PerfView with /DotNetAllocSampled to pinpoint the exact allocating types and call sites."}
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
