Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 18)
- p95 Latency: 535.98694ms
- Requests/sec: 1052.1
- Error rate: 0%
- Improvement vs baseline: 92.9%

## Baseline Performance
- p95 Latency: 7546.103045ms
- Requests/sec: 125.5
- Error rate: 0%

## Runtime Counters
- CPU avg: 14.83%
- GC heap max: 1086MB
- Gen2 collections: 0
- Thread pool max threads: 41

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


## Known Optimization Queue
- [TRIED] `SampleApi/Data/AppDbContext.cs` — Add database indexes on high-traffic filter columns *(experiment 16 — improved)*
- [TRIED] `SampleApi/Pages/Products/Detail.cshtml.cs` — Add AsNoTracking to all read-only queries on Detail page *(experiment 15 — improved)*
- [TRIED] `SampleApi/Pages/Checkout/Index.cshtml.cs` — Add AsNoTracking and Select projections to Checkout read queries *(experiment 17 — improved)*

## Last Experiment's Fix
Add AsNoTracking and Select projections to Checkout read queries

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


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":2.18,"exclusivePct":2.18,"callChain":["Controller Action","EF Core ToListAsync","SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun","TdsParserStateObject.TryReadChar"],"observation":"Highest exclusive-sample application-mode function. Indicates the API is reading a very large volume of character data from SQL Server — likely fetching too many rows or oversized string/nvarchar columns per request."},{"method":"System.Text.Json.Serialization.Converters.StringConverter.Write","inclusivePct":1.07,"exclusivePct":1.07,"callChain":["Controller Action","JsonSerializer.SerializeAsync","ObjectDefaultConverter.OnTryWrite","GetMemberAndWriteJson","StringConverter.Write"],"observation":"JSON string serialization is the top serialization cost, reinforcing that responses contain many or large string properties. Reducing payload size (pagination, field selection, DTO projection) would cut both SQL read and JSON write costs."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1+AsyncEnumerator.MoveNextAsync","inclusivePct":8.5,"exclusivePct":0.33,"callChain":["Controller Action","ToListAsync","ConfiguredCancelableAsyncEnumerable.MoveNextAsync","SingleQueryingEnumerable.MoveNextAsync"],"observation":"High inclusive but low exclusive — this is the EF Core row-iteration loop driving all SqlClient reads beneath it. The sheer volume of iterations suggests queries return large result sets without server-side filtering or pagination."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.TryReadColumnInternal + PrepareAsyncInvocation","inclusivePct":1.22,"exclusivePct":1.22,"callChain":["SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TryReadInternal","TryReadColumnInternal / PrepareAsyncInvocation"],"observation":"Column-level read overhead (1713 + 1708 samples) shows many columns being materialized per row. Using DTO projections (Select) instead of loading full entities would eliminate unnecessary column reads."},{"method":"System.Runtime.CompilerServices.CastHelpers (aggregate: IsInstanceOfInterface, IsInstanceOfClass, ChkCast*)","inclusivePct":3.19,"exclusivePct":3.19,"callChain":["Various — DI resolution, EF Core materialization, LINQ pipeline","CastHelpers.IsInstanceOfInterface / IsInstanceOfClass / ChkCastAny"],"observation":"Unusually high type-casting overhead (~8965 samples aggregate) suggests heavy polymorphic dispatch, boxing in LINQ pipelines, or EF Core materializing through reflection. Strongly-typed projections and reducing interface-heavy hot paths would help."},{"method":"Microsoft.Extensions.DependencyInjection ResolveService + ServiceProvider.GetService","inclusivePct":0.73,"exclusivePct":0.37,"callChain":["Kestrel request pipeline","Controller activation","ServiceProvider.GetService","ResolveService (dynamicClass)"],"observation":"DI resolution appears in the hot path (764 + 266 samples) with Dictionary lookups for ServiceCacheKey (712 + 262 samples). This may indicate transient services being resolved repeatedly per request. Consider caching resolved services or reducing the dependency graph depth."},{"method":"System.Text.UnicodeEncoding.GetCharCount + GetChars","inclusivePct":0.9,"exclusivePct":0.9,"callChain":["TdsParser.TryReadPlpUnicodeCharsChunk","UnicodeEncoding.GetCharCount / GetChars","Buffer.Memmove"],"observation":"Unicode decoding of SQL string data (1412 + 1113 + 1282 memmove samples). Directly proportional to the volume of string data read from the database. Reducing fetched columns or using varchar instead of nvarchar where possible would reduce this."},{"method":"System.Threading.SemaphoreSlim.Wait (synchronous)","inclusivePct":0.11,"exclusivePct":0.11,"callChain":["Async pipeline","SemaphoreSlim.Wait"],"observation":"Synchronous Wait (309 samples) in an async pipeline risks thread pool starvation under load. Should be WaitAsync. Combined with SpinWait.SpinOnceCore (279 samples), indicates contention on a shared resource."},{"method":"System.Reflection.DefaultBinder.SelectMethod + RuntimeType.GetMethodImplCommon","inclusivePct":0.56,"exclusivePct":0.33,"callChain":["Request pipeline / model binding","RuntimeType.GetMethodImplCommon","DefaultBinder.SelectMethod","FilterApplyMethodBase"],"observation":"Reflection-based method resolution in the hot path (557 + 359 + 247 samples). Likely from model binding or non-compiled expression trees. Ensuring EF Core compiled queries or pre-compiled model binders could eliminate this."},{"method":"Microsoft.Extensions.Logging.Console.AnsiParser.Parse + MessageLogger.IsEnabled","inclusivePct":0.19,"exclusivePct":0.19,"callChain":["EF Core / Kestrel logging","MessageLogger.IsEnabled","ConsoleLogger","AnsiParser.Parse"],"observation":"Console logging with ANSI parsing (264 + 261 samples) is active during load testing. Console logging is synchronous and slow; disable or raise the minimum log level for production/perf-test runs."}],"summary":"CPU time is dominated by SQL data reading (~7.6% aggregate in TDS parser functions) and JSON response serialization (~2.7%), indicating the API fetches and serializes large, unfiltered result sets. The most impactful optimization would be adding server-side pagination or DTO projections to reduce both the volume of data read from SQL Server and the JSON serialization cost. Secondary concerns include unusually high type-casting overhead (3.2%) suggesting materialization inefficiency, synchronous SemaphoreSlim.Wait risking thread pool starvation, reflection in the hot path hinting at non-compiled queries, and console logging adding unnecessary overhead during load. The developer should focus first on the query layer — ensuring endpoints use pagination, column projection (Select), and compiled queries — then address the sync-over-async Wait and disable verbose logging under load."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":105,"gen1Rate":89,"gen2Rate":5,"pauseTimeMs":{"avg":5.7,"max":67.9,"total":1138.2},"gcPauseRatio":1.0,"fragmentationPct":0.0,"observations":["Gen1 count (89) is abnormally close to Gen0 count (105) — 85% promotion ratio indicates most objects survive Gen0 but die in Gen1, suggesting mid-lived allocations (e.g., request-scoped buffers, EF tracking objects, or LINQ materializations held across async awaits)","Gen0 max pause of 51.8ms and Gen1 max pause of 67.9ms both exceed the 50ms threshold that directly impacts p95 latency (currently 536ms) — these GC pauses are contributing to tail latency spikes","Gen2 collections are low (5) with small pauses (max 5.9ms) — long-lived object management is healthy and LOH pressure is minimal","GC pause ratio of 1.0% is within acceptable range, but the 1138ms total pause time across a load test means individual requests are periodically stalled by GC","Total allocation of ~49GB is extremely high — the runtime is churning through massive volumes of short-to-mid-lived objects, driving the high collection counts"]},"heapAnalysis":{"peakSizeMB":1063.86,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 1064MB (over 1GB) under load is very large for an API service — this suggests large object graphs are being held in memory simultaneously, likely from EF Core change tracking or large result set materialization","Zero fragmentation is good — LOH compaction is not an issue, and pinned objects are not causing memory holes","With 49GB total allocated and a 1GB peak, objects are being collected but the sheer volume indicates allocation-heavy code paths that should be optimized with pooling or caching"]},"topAllocators":[{"type":"(allocation type data not captured)","allocMB":null,"pctOfTotal":null,"callSite":"Unknown — allocation tick sampling was not available in this trace","observation":"Re-run PerfView with /DotNetAllocSampled to capture allocation tick events, then export via 'GC Heap Alloc Stacks' view to identify the specific types and call sites driving the 49GB allocation volume"}],"summary":"The dominant issue is an extremely high Gen0-to-Gen1 promotion ratio (85%), meaning most allocated objects survive just long enough to escape Gen0 — a pattern typical of EF Core change-tracked entities, LINQ intermediate collections, or request-scoped buffers held across async boundaries. This drives 89 Gen1 collections with pause spikes up to 67.9ms that directly contribute to the 536ms p95 latency. The #1 fix should be reducing mid-lived allocations: disable EF Core change tracking for read-only queries (AsNoTracking), pool or reuse request-scoped buffers with ArrayPool<T>, and avoid materializing large collections with ToList() when streaming with IAsyncEnumerable would suffice. Allocation type data was not captured — re-collect with /DotNetAllocSampled to pinpoint exact hotspots."}
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
