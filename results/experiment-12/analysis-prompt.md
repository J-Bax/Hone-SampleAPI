Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 12)
- p95 Latency: 548.443835ms
- Requests/sec: 1006.2
- Error rate: 0%
- Improvement vs baseline: 92.7%

## Baseline Performance
- p95 Latency: 7546.103045ms
- Requests/sec: 125.5
- Error rate: 0%

## Runtime Counters
- CPU avg: 12.89%
- GC heap max: 987MB
- Gen2 collections: 0
- Thread pool max threads: 68

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


## Known Optimization Queue
- [TRIED] `SampleApi/Controllers/OrdersController.cs` — Batch product lookups and add AsNoTracking in CreateOrder *(experiment 10 — improved)*
- [TRIED] `SampleApi/Pages/Checkout/Index.cshtml.cs` — Consolidate redundant SaveChangesAsync round trips in checkout post *(experiment 11 — improved)*
- [PENDING] [ARCHITECTURE] `SampleApi/Data/AppDbContext.cs` — Add database indexes for frequently filtered columns

## Last Experiment's Fix
Consolidate redundant SaveChangesAsync round trips in checkout post

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


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":2.24,"exclusivePct":2.24,"callChain":["EF Core Query Pipeline","RelationalCommand.ExecuteReaderAsync","TdsParser.TryRun","TdsParser.TryReadSqlStringValue","TdsParserStateObject.TryReadChar"],"observation":"Highest exclusive-sample application-layer method. Reading individual characters from the TDS stream at high volume indicates large string columns or many rows being read per request — classic sign of over-fetching (SELECT * or missing pagination)."},{"method":"Microsoft.Data.SqlClient (aggregate TDS parsing layer)","inclusivePct":9.48,"exclusivePct":9.48,"callChain":["SqlDataReader.ReadAsync","TdsParser.TryRun","TryReadColumnInternal","TryReadSqlValue","TryReadSqlStringValue","TryReadChar"],"observation":"26 distinct SqlClient methods collectively consume ~26,600 exclusive samples — the single largest application-layer cost center. The spread across TryReadChar, TryReadColumnInternal, PrepareAsyncInvocation, snapshot management, and string reading strongly suggests queries returning too many rows and/or too many columns. Adding server-side pagination, column projection (SELECT only needed fields), and compiled queries would significantly reduce this."},{"method":"System.Text.Json.Serialization.Converters.StringConverter.Write","inclusivePct":1.05,"exclusivePct":1.05,"callChain":["JsonSerializer.SerializeAsync","ObjectDefaultConverter.OnTryWrite","JsonPropertyInfo.GetMemberAndWriteJson","StringConverter.Write"],"observation":"Top JSON serialization hotspot — writing string properties dominates serialization cost. Combined with high SQL string-reading, this confirms the API is fetching and serializing large volumes of string data. Reducing payload size (fewer fields, shorter strings, pagination) would cut both SQL read and JSON write costs."},{"method":"System.Text.Json (aggregate serialization layer)","inclusivePct":2.9,"exclusivePct":2.9,"callChain":["Controller Action","JsonSerializer.SerializeAsync","ObjectDefaultConverter.OnTryWrite","StringConverter.Write / WriteStringMinimized / WritePropertyNameSection"],"observation":"~8,100 exclusive samples across 11 System.Text.Json methods. WriteStack push/pop overhead and property-level serialization cost suggest deeply nested or wide response objects. Consider using JsonSerializerContext (source generators) to eliminate reflection, and reduce response payload size."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable.MoveNextAsync","inclusivePct":0.31,"exclusivePct":0.31,"callChain":["ToListAsync","SingleQueryingEnumerable.AsyncEnumerator.MoveNextAsync","RelationalCommand.ExecuteReaderAsync","SqlDataReader.ReadAsync"],"observation":"EF Core query enumeration — the inclusive cost is much higher (includes all SqlClient time beneath it). The presence of SingleQueryingEnumerable suggests individual query execution per call. If multiple queries fire per request (N+1 pattern), switching to eager loading (.Include()) or a single projected query would help."},{"method":"Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync","inclusivePct":0.14,"exclusivePct":0.14,"callChain":["Controller Action","ToListAsync","SingleQueryingEnumerable.MoveNextAsync"],"observation":"ToListAsync materializes entire result sets into memory. Combined with the massive TDS read cost below it, this confirms full-table or large unbounded queries being materialized. Add .Take(N) or keyset pagination to limit result sizes."},{"method":"System.Runtime.CompilerServices.CastHelpers (aggregate type casting)","inclusivePct":3.13,"exclusivePct":3.13,"callChain":["Various framework pipelines","IsInstanceOfInterface / IsInstanceOfClass / ChkCastAny / StelemRef"],"observation":"~8,800 exclusive samples in type-checking and casting. This is driven by polymorphic DI resolution, EF Core materialization, and ASP.NET middleware pipeline — largely framework overhead. Not directly actionable, but reducing object allocations and DI resolutions per request would lower this indirectly."},{"method":"Microsoft.Extensions.DependencyInjection.ServiceProvider.GetService + ResolveService","inclusivePct":0.76,"exclusivePct":0.76,"callChain":["ASP.NET Request Pipeline","ServiceProvider.GetService","ResolveService","Dictionary.FindValue(ServiceCacheKey)"],"observation":"DI resolution with associated dictionary lookups costs ~2,100 samples per measurement window. Indicates many transient service resolutions per request. Consider converting frequently-resolved transient services to scoped lifetime, or injecting factories instead of resolving repeatedly."},{"method":"System.Threading.ExecutionContext.Capture + async overhead","inclusivePct":1.11,"exclusivePct":1.11,"callChain":["async/await state machine","AsyncMethodBuilderCore.Start","ExecutionContext.Capture","ExecutionContext.RunInternal"],"observation":"~3,100 samples in async machinery — ExecutionContext capture/restore and task scheduling. This is proportional to the number of async calls per request. Reducing unnecessary async layers (e.g., removing async wrappers around synchronous code) and batching DB calls would lower this."},{"method":"Microsoft.AspNetCore.Mvc.ViewFeatures.Buffers.ViewBuffer.WriteToAsync","inclusivePct":0.11,"exclusivePct":0.11,"callChain":["MVC Pipeline","ViewResultExecutor","ViewBuffer.WriteToAsync"],"observation":"Unexpected for a Web API — this indicates Razor view rendering is in the response pipeline. If the API returns JSON, ensure controllers return ObjectResult/JsonResult rather than ViewResult to eliminate view rendering overhead entirely."}],"summary":"The CPU profile is dominated by SQL data reading (~9.5% exclusive across 26+ SqlClient methods) and JSON serialization (~2.9%), strongly indicating the API fetches large, unbounded result sets and serializes wide response objects. The top optimization targets are: (1) add server-side pagination or .Take(N) to EF Core queries to drastically reduce rows read and serialized, (2) use column projection (select only needed fields) to cut per-row TDS parsing and JSON property writing, and (3) verify controllers return JSON directly rather than through Razor views. Addressing the data volume problem will cascade improvements across SQL reading, JSON serialization, type casting, async overhead, and GC pressure simultaneously."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.87,"gen1Rate":0.75,"gen2Rate":0.04,"pauseTimeMs":{"avg":5.3,"max":29.2,"total":1048.4},"gcPauseRatio":0.9,"fragmentationPct":0.0,"observations":["Gen1/Gen0 ratio is abnormally high (90/104 = 86.5%) — nearly every Gen0 collection promotes surviving objects into Gen1, indicating objects consistently outlive Gen0 but die in Gen1. This pattern is characteristic of async/await state machines, EF Core DbContext tracking, or objects captured in closures that span await boundaries.","Gen2 collections are very low (5 total) with short pauses (max 4.1ms) — long-lived object management and LOH behavior are healthy.","Max GC pause of 29.2ms (Gen1) is moderate and below the 50ms concern threshold, so individual GC pauses are not the primary driver of the 548ms p95 latency.","Total GC pause ratio of 0.9% is well within healthy range (<5%), meaning GC is not starving application threads of CPU time.","Despite healthy pause ratios, the sheer volume of allocations (48.3GB over the test) forces 199 total collections, creating cumulative throughput drag even if each pause is short."]},"heapAnalysis":{"peakSizeMB":1050.78,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 1.05GB is very large for a Web API under load — this suggests significant mid-lived object retention, likely EF Core change tracking caches, materialized query result sets, or large response serialization buffers.","Total allocation volume of 48.3GB during the test (~402 MB/sec) is extremely high and the primary driver of GC frequency. This volume points to per-request allocations of large object graphs rather than efficient streaming or pooling.","Zero fragmentation is positive — LOH compaction issues and pinned buffer problems are not a concern."]},"topAllocators":[{"type":"(allocation tick data not captured)","allocMB":null,"pctOfTotal":null,"callSite":"Unknown — allocation sampling was not enabled or did not record type-level data","observation":"Re-run PerfView with /DotNetAllocSampled to capture allocation tick events and identify which types and call sites are responsible for the 48.3GB allocation volume. Without this data, optimization must be guided by the GC generation pattern analysis below."},{"type":"Likely: EF Core materialized entities","allocMB":null,"pctOfTotal":null,"callSite":"DbContext query execution → entity materialization and change tracker entries","observation":"The high Gen1 survival pattern strongly suggests EF Core is materializing large result sets with change tracking enabled. Use AsNoTracking() for read-only queries to eliminate change tracker allocations, and add pagination or streaming to avoid materializing entire tables."},{"type":"Likely: System.Byte[] / serialization buffers","allocMB":null,"pctOfTotal":null,"callSite":"JSON serialization of large response payloads → System.Text.Json buffer allocations","observation":"Large API response bodies require proportionally large serialization buffers. If endpoints return unbounded collections, each request allocates significant byte arrays. Use IAsyncEnumerable<T> streaming or paginate results to reduce per-request buffer sizes."},{"type":"Likely: async state machines / Task<T>","allocMB":null,"pctOfTotal":null,"callSite":"Async controller actions → compiler-generated state machine structs boxed to heap","observation":"Deep async call chains (controller → service → EF → SQL) generate multiple state machine allocations per request. At 1006 RPS this accumulates rapidly. ValueTask<T> and pooled async state machines (.NET 6 AsyncMethodBuilderAttribute) can reduce this overhead."}],"summary":"The dominant memory issue is extreme allocation volume (48.3GB, ~402 MB/sec) combined with an abnormal Gen1 promotion rate of 86.5%, indicating that per-request objects consistently survive Gen0 — a hallmark of EF Core change tracking and large materialized result sets held across async boundaries. While GC pause ratios are healthy at 0.9%, the 1.05GB peak heap and relentless allocation churn create throughput drag contributing to the 548ms p95 latency. The #1 optimization target is reducing per-request allocation volume: add AsNoTracking() to read-only EF Core queries, paginate large result sets to avoid materializing full tables, and re-run with /DotNetAllocSampled to confirm the exact allocation hotspots."}
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
