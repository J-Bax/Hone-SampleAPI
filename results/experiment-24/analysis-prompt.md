Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 24)
- p95 Latency: 546.113655ms
- Requests/sec: 1100.1
- Error rate: 0%
- Improvement vs baseline: 92.8%

## Baseline Performance
- p95 Latency: 7546.103045ms
- Requests/sec: 125.5
- Error rate: 0%

## Runtime Counters
- CPU avg: 15.19%
- GC heap max: 1065MB
- Gen2 collections: 33999504
- Thread pool max threads: 34

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


## Known Optimization Queue
- [TRIED] `SampleApi/Controllers/ProductsController.cs` — Eliminate redundant category existence DB round trip in GetProductsByCategory *(experiment 21 — improved)*
- [TRIED] `SampleApi/Pages/Cart/Index.cshtml.cs` — Add Select projection to product lookup in Cart page LoadCart *(experiment 22 — improved)*
- [TRIED] `SampleApi/Pages/Orders/Index.cshtml.cs` — Add Select projection to product name lookup in Orders page *(experiment 23 — improved)*

## Last Experiment's Fix
Add Select projection to product name lookup in Orders page

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


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":1.24,"exclusivePct":1.24,"callChain":["EF Core SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun","TdsParserStateObject.TryReadChar"],"observation":"Highest exclusive-time application-layer method. Character-by-character TDS stream reading indicates the API is fetching a large volume of string-heavy column data from SQL Server — likely over-fetching columns or rows (SELECT * anti-pattern or missing pagination)."},{"method":"Microsoft.Data.SqlClient (TDS Parser + SqlDataReader aggregate)","inclusivePct":4.08,"exclusivePct":4.08,"callChain":["EF Core ToListAsync","SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun","TdsParser.TryReadSqlValue/TryReadSqlStringValue/TryReadPlpUnicodeCharsChunk"],"observation":"22,000+ combined samples across 20+ TDS parsing and SqlDataReader methods (TryReadColumnInternal:1847, PrepareAsyncInvocation:1513, TryGetTokenLength:1245, TryReadPlpUnicodeCharsChunk:1242, StateSnapshot.Snap:1061, etc.). The sheer breadth of SQL data-reading overhead points to queries returning excessive result sets — consider projecting only needed columns via .Select() and adding server-side pagination."},{"method":"sqlmin + sqllang + sqldk + sqltses (SQL Server Engine)","inclusivePct":16.98,"exclusivePct":16.98,"callChain":["Application Query","SqlCommand.ExecuteReaderAsync","TDS Network","SQL Server Engine (sqlmin/sqllang/sqldk/sqltses)"],"observation":"~91,700 combined samples in SQL Server engine modules — the single largest CPU consumer. This strongly suggests missing indexes, table scans, or complex query plans. Run SET STATISTICS IO ON or examine query plans for the dominant endpoints to find scan-heavy operations."},{"method":"System.Text.Json.Serialization.Converters.StringConverter.Write","inclusivePct":0.57,"exclusivePct":0.57,"callChain":["Controller Action","JsonSerializer.SerializeAsync","ObjectDefaultConverter.OnTryWrite","JsonPropertyInfo.GetMemberAndWriteJson","StringConverter.Write"],"observation":"3,074 samples in string serialization alone, with ~7,600 total across JSON serialization methods (ObjectDefaultConverter:961, JsonWriterHelper.ToUtf8:989, TextEncoder:933, WriteStack.Push:432). The API response payloads contain many string properties — consider returning DTOs with fewer fields, or using source-generated System.Text.Json serializers to reduce overhead."},{"method":"System.Runtime.CompilerServices.CastHelpers (aggregate)","inclusivePct":1.71,"exclusivePct":1.71,"callChain":["EF Core Materialization","CastHelpers.IsInstanceOfInterface/IsInstanceOfClass/ChkCastAny/ChkCastInterface/StelemRef"],"observation":"~9,200 combined samples across type-casting helpers (IsInstanceOfInterface:3088, IsInstanceOfClass:2706, ChkCastAny:735, ChkCastInterface:637, StelemRef:841+460). This runtime overhead is driven by EF Core entity materialization and polymorphic collection operations. Projecting to lightweight DTOs instead of full entity graphs would reduce this."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1.MoveNextAsync","inclusivePct":0.18,"exclusivePct":0.18,"callChain":["Controller Action","ToListAsync","SingleQueryingEnumerable.MoveNextAsync"],"observation":"993 exclusive samples in EF Core's async row enumeration plus 365 in ToListAsync. Combined with the massive SQL client overhead beneath it, this confirms the query-materialization pipeline is the critical path. Consider using .AsNoTracking(), projections, or compiled queries to reduce per-row overhead."},{"method":"System.Text.UnicodeEncoding.GetCharCount + GetChars","inclusivePct":0.51,"exclusivePct":0.51,"callChain":["TdsParser.TryReadSqlStringValue","String.CreateStringFromEncoding","UnicodeEncoding.GetCharCount/GetChars"],"observation":"2,740 combined samples in Unicode decoding (GetCharCount:1704, GetChars:1036) plus 387 in String.CreateStringFromEncoding. This is the cost of materializing SQL NVARCHAR columns into .NET strings — further evidence of reading too many or too-large string columns from the database."},{"method":"Microsoft.Extensions.DependencyInjection.ResolveService","inclusivePct":0.29,"exclusivePct":0.16,"callChain":["Kestrel Request Pipeline","DI Container","ResolveService","Dictionary.FindValue(ServiceCacheKey)"],"observation":"861 samples in DI resolution plus 705 in ServiceCacheKey dictionary lookups. This suggests either too many transient service registrations or per-request resolution of services that could be scoped or singleton. Review service lifetimes for frequently-resolved dependencies like DbContext, repositories, or mappers."},{"method":"System.Threading.ExecutionContext (async overhead aggregate)","inclusivePct":0.54,"exclusivePct":0.54,"callChain":["AsyncMethodBuilderCore.Start","ExecutionContext.Capture/RunInternal/SetLocalValue/OnValuesChanged"],"observation":"~2,900 combined samples in async execution context management (Start:807, Capture:703, OnValuesChanged:582, SetLocalValue:484, RunInternal:359). This is the tax of many small async operations in the request pipeline. Reducing the number of async state machines (e.g., batching DB calls, reducing middleware) would lower this overhead."},{"method":"System.Threading.SemaphoreSlim.Wait","inclusivePct":0.06,"exclusivePct":0.06,"callChain":["Request Pipeline","SemaphoreSlim.Wait"],"observation":"332 samples in synchronous SemaphoreSlim.Wait (not WaitAsync). Synchronous blocking on an async code path causes thread pool starvation under load. This should be converted to await SemaphoreSlim.WaitAsync() — likely in connection pool or resource throttling code."}],"summary":"The CPU profile is overwhelmingly dominated by data access: SQL Server engine processing (~17% of samples) combined with TDS parsing and SqlDataReader overhead (~4%) indicates the API is executing expensive queries that return large result sets with many string columns. The top optimization targets are: (1) Add missing database indexes and optimize query plans to reduce the massive SQL Server engine CPU time; (2) Use EF Core projections (.Select()) with .AsNoTracking() to fetch only needed columns instead of full entity graphs, which will also reduce the type-casting, Unicode decoding, and JSON serialization overhead downstream; (3) Investigate the synchronous SemaphoreSlim.Wait for potential thread pool starvation under load."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.9,"gen1Rate":0.78,"gen2Rate":0.04,"pauseTimeMs":{"avg":7.0,"max":113.6,"total":1417.7},"gcPauseRatio":1.2,"fragmentationPct":0.0,"observations":["Gen1 count (92) is nearly equal to Gen0 count (106), meaning ~87% of Gen0 collections promote surviving objects into Gen1. This is abnormal — objects are living just long enough to escape Gen0 but not long enough for Gen2, creating a 'mid-life crisis' pattern that maximizes GC work.","Max Gen1 pause of 113.6ms directly contributes to the 546ms p95 latency — a single GC pause consuming 20% of a request's budget is severe. Gen0 max pause of 71.3ms is also high.","Gen2 collections are healthy at only 5 with low pause times (max 3.5ms), indicating long-lived objects are well-managed and LOH pressure is minimal.","Total allocations of 51,151 MB over ~118s implies an allocation rate of ~433 MB/sec. This extreme allocation velocity is the root driver of frequent Gen0/Gen1 collections.","GC pause ratio of 1.2% is below the 5% concern threshold, but the tail latency impact from max pauses (113.6ms) is the real problem — ratio averages hide the worst-case spikes that hurt p95."]},"heapAnalysis":{"peakSizeMB":1110.11,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 1,110 MB is very large for an API service under load. This indicates either large object graphs held in memory (caches, materialized collections) or a high watermark from concurrent request processing.","Zero fragmentation is positive — LOH compaction is not a concern. The issue is pure allocation volume, not memory layout.","With 51,151 MB total allocated and a 1,110 MB peak, the heap is turning over ~46x during the test. Objects are being allocated and collected at an extremely high rate."]},"topAllocators":[{"type":"(unavailable — allocation tick data not captured)","allocMB":null,"pctOfTotal":null,"callSite":null,"observation":"Allocation sampling was not enabled or produced no data. To identify the top allocating types, re-run PerfView with /DotNetAllocSampled and export the 'GC Heap Alloc Stacks' view. Given 433 MB/sec allocation rate, identifying the top allocators is critical for targeted optimization."}],"summary":"The dominant issue is an extreme allocation rate (~433 MB/sec, 51 GB total) causing near-parity Gen0/Gen1 collection counts — a classic mid-life crisis where objects survive Gen0 only to be collected in Gen1, producing costly 113ms max pauses that directly inflate p95 latency. The #1 fix priority is reducing allocation volume in hot request paths: look for large temporary collections (ToList/ToArray on EF queries), excessive string building, or repeated deserialization. Object pooling (ArrayPool<T>, ObjectPool<T>) and reducing LINQ materializations in controller actions would cut both allocation rate and GC pause severity. Re-enable allocation tick sampling to pinpoint the exact types and call sites responsible."}
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
