Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 15)
- p95 Latency: 453.409225ms
- Requests/sec: 1420.3
- Error rate: 11.11%
- Improvement vs baseline: 77.9%

## Baseline Performance
- p95 Latency: 2054.749925ms
- Requests/sec: 427.3
- Error rate: 11.11%

## Runtime Counters
- CPU avg: 17.67%
- GC heap max: 1248MB
- Gen2 collections: 31322424
- Thread pool max threads: 36

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
  const addToCartPageRes = http.post(
    `${BASE_URL}/Products/Detail/${cartProductId}`,
    { productId: String(cartProductId), quantity: '1' }
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
  const checkoutSubmitRes = http.post(
    `${BASE_URL}/Checkout`,
    { customerName: customerName }
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
| 1 | 2026-03-13 14:40 | `SampleApi/Controllers/CartController.cs` | Cart endpoints: full-table scans, N+1 queries, and per-item saves | improved |
| 2 | 2026-03-13 14:41 | `SampleApi/Controllers/ProductsController.cs` | Product search and category filter use client-side evaluation | improved |
| 2 | 2026-03-13 19:03 | `SampleApi/Controllers/ReviewsController.cs` | Review queries load entire table instead of filtering server-side | improved |
| 3 | 2026-03-13 19:06 | `SampleApi/Pages/Products/Detail.cshtml.cs` | Product detail page loads entire Reviews and Products tables | improved |
| 1 | 2026-03-14 12:25 | `SampleApi/Pages/Index.cshtml.cs` | Home page loads all products and reviews for sampling | improved |
| 2 | 2026-03-14 12:25 | `SampleApi/Controllers/OrdersController.cs` | Build failure: CreateOrder has N+1 product lookups and double SaveChanges | regressed |
| 3 | 2026-03-14 13:02 | `SampleApi/Pages/Orders/Index.cshtml.cs` | Orders page loads entire Orders and OrderItems tables with N+1 product lookups | improved |
| 4 | 2026-03-14 13:26 | `SampleApi/Pages/Checkout/Index.cshtml.cs` | Checkout page has per-item SaveChanges, full table scans, and N+1 queries | improved |
| 5 | 2026-03-14 13:52 | `SampleApi/Pages/Cart/Index.cshtml.cs` | Cart page loads entire CartItems table and has N+1 product lookups | improved |
| 6 | 2026-03-14 14:30 | `SampleApi/Pages/Products/Index.cshtml.cs` | Products page loads entire Products table for client-side pagination | regressed |
| 7 | 2026-03-14 14:55 | `SampleApi/Pages/Products/Detail.cshtml.cs` | Product detail OnPost loads entire CartItems table to find one row | regressed |
| 8 | 2026-03-14 15:39 | `SampleApi/Controllers/ProductsController.cs` | Add AsNoTracking to all read-only query endpoints | regressed |
| 9 | 2026-03-14 16:04 | `SampleApi/Controllers/ReviewsController.cs` | Replace tracked FindAsync existence checks with AnyAsync and add AsNoTracking | improved |
| 10 | 2026-03-14 16:27 | `SampleApi/Program.cs` | Test failure: Enable DbContext pooling to reduce per-request allocation and GC pressure | regressed |
| 11 | 2026-03-14 17:11 | `SampleApi/Pages/Products/Index.cshtml.cs` | Products page tracks 1000 entities needlessly on read-only render | improved |
| 12 | 2026-03-14 17:35 | `SampleApi/Controllers/OrdersController.cs` | OrdersController has N+1 queries and full table scans across multiple endpoints | improved |
| 13 | 2026-03-14 18:00 | `SampleApi/Pages/Products/Detail.cshtml.cs` | Product detail page uses tracked queries for read-only rendering | improved |
| 14 | 2026-03-14 19:13 | `SampleApi/Controllers/ProductsController.cs` | GetProducts re-queries all 1000 products from the database on every request | improved |


## Known Optimization Queue
- [TRIED] `SampleApi/Controllers/ProductsController.cs` — GetProducts re-queries all 1000 products from the database on every request *(experiment 14 — improved)*
- [PENDING] [ARCHITECTURE] `SampleApi/Data/AppDbContext.cs` — Missing database indexes on high-traffic filter columns

## Last Experiment's Fix
GetProducts re-queries all 1000 products from the database on every request

## Experiment History (with metrics)
Do NOT re-attempt optimizations that were already tried and resulted in stale or regressed outcomes. Propose different targets or approaches instead.
| Exp | File | Outcome | p95 (ms) | RPS | Branch |
|-----|------|---------|----------|-----|--------|
| 1 | — | improved | 1641.9 | 533.7 | hone/experiment-1 |
| 2 | — | build_failure | N/A | N/A | hone/experiment-2 |
| 3 | — | improved | 480.1 | 1352.2 | hone/experiment-3 |
| 4 | — | improved | 481.5 | 1323.9 | hone/experiment-4 |
| 5 | — | improved | 482.4 | 1321.2 | hone/experiment-5 |
| 6 | — | regressed | 651.1 | 1115.7 | hone/experiment-6 |
| 7 | — | regressed | 616.6 | 1109.2 | hone/experiment-7 |
| 8 | — | regressed | 561.9 | 1192.9 | hone/experiment-8 |
| 9 | — | improved | 496.7 | 1295.4 | hone/experiment-9 |
| 10 | — | test_failure | N/A | N/A | hone/experiment-10 |
| 11 | — | improved | 486.5 | 1301.3 | hone/experiment-11 |
| 12 | — | improved | 471.7 | 1327.8 | hone/experiment-12 |
| 13 | — | improved | 474 | 1325.1 | hone/experiment-13 |
| 14 | — | improved | 453.4 | 1420.3 | hone/experiment-14 |


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1+AsyncEnumerator.MoveNextAsync","inclusivePct":12.8,"exclusivePct":0.3,"callChain":["ToListAsync","SingleQueryingEnumerable.MoveNextAsync","TdsParser.TryRun","TryReadColumnInternal","TryReadChar"],"observation":"EF Core query materialization is the dominant inclusive hotspot, driving all SQL client data reading beneath it. High inclusive with low exclusive indicates this is the call-site funneling into expensive row-by-row materialization — likely fetching too many rows or columns (over-fetching / missing projection)."},{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":2.3,"exclusivePct":2.3,"callChain":["SingleQueryingEnumerable.MoveNextAsync","TdsParser.TryRun","TryReadSqlStringValue","TryReadPlpUnicodeCharsChunk","TryReadChar"],"observation":"Highest exclusive-sample method in application-adjacent code (3661 samples). Character-by-character reading of SQL string columns dominates CPU. Combined with TryReadPlpUnicodeCharsChunk (607) and UnicodeEncoding.GetChars (601), this indicates the query returns large or numerous string columns — a classic sign of SELECT * or unbounded text fields."},{"method":"System.Text.Json.Serialization.Converters.StringConverter.Write","inclusivePct":2.4,"exclusivePct":0.7,"callChain":["ObjectDefaultConverter.OnTryWrite","GetMemberAndWriteJson","StringConverter.Write","WriteStringMinimized","ToUtf8"],"observation":"JSON serialization of string properties consumes ~3700 samples across the serializer stack. Combined with OptimizedInboxTextEncoder (459 samples for HTML-encoding checks), this suggests the response payloads contain many or large string properties. Reducing projected columns or using lighter DTOs would cut serialization cost."},{"method":"System.Runtime.CompilerServices.CastHelpers.IsInstanceOfClass + IsInstanceOfInterface","inclusivePct":2.9,"exclusivePct":2.9,"callChain":["[EF Core materialization / DI resolution]","CastHelpers.IsInstanceOfClass","CastHelpers.IsInstanceOfInterface"],"observation":"Type-checking and casting operations total ~4650 samples (IsInstanceOfClass 1192, IsInstanceOfInterface 1122, ChkCastAny 845, ChkCastInterface 568, IsInstanceOfAny 315). This level of polymorphic dispatch suggests heavy use of object-typed collections, boxed value types, or EF Core's internal materialization pipeline working overtime due to complex entity graphs."},{"method":"Microsoft.Data.SqlClient.TdsParserStateObject+StateSnapshot.Snap","inclusivePct":0.8,"exclusivePct":0.3,"callChain":["SqlDataReader.ReadAsync","PrepareAsyncInvocation","SetSnapshot","StateSnapshot.Snap"],"observation":"SQL async read infrastructure (Snap 501, PrepareAsyncInvocation 889, Clear 278, SetSnapshot 240, PushBuffer 196) totals ~2100 samples. Each row read takes a state snapshot for async cancellation support. Reading fewer rows directly reduces this overhead."},{"method":"System.Collections.Generic.SortedDictionary`2+Enumerator.MoveNext","inclusivePct":0.6,"exclusivePct":0.6,"callChain":["[Application code]","SortedDictionary.Enumerator.MoveNext","SortedSet.Enumerator.MoveNext"],"observation":"SortedDictionary/SortedSet iteration totals ~937 samples (SortedSet.MoveNext 329, ValueCollection.MoveNext 272, Enumerator.MoveNext 202, get_Current 134). Tree-based sorted collections have poor cache locality vs. List+Sort or Dictionary. If ordering is only needed for output, sort once after collection rather than maintaining sorted state."},{"method":"Microsoft.Extensions.DependencyInjection.ServiceProvider.ResolveService","inclusivePct":0.6,"exclusivePct":0.2,"callChain":["Controller action","ServiceProvider.GetService","ResolveService","Dictionary.FindValue(ServiceCacheKey)"],"observation":"DI resolution (ResolveService 386, GetService 143, ServiceCacheKey lookups 316+129) totals ~974 samples. This suggests transient services being resolved per-request with non-trivial dependency graphs. Consider scoped or singleton lifetimes for stable services, or injecting factories for conditional resolution."},{"method":"System.DefaultBinder.SelectMethod","inclusivePct":0.4,"exclusivePct":0.2,"callChain":["[Request pipeline]","RuntimeType.GetMethodImplCommon","DefaultBinder.SelectMethod"],"observation":"Reflection-based method selection (SelectMethod 311, GetMethodImplCommon 180, CerHashtable 175) totals ~666 samples on the hot path. This is unexpected in steady-state and may indicate model binding or serialization using runtime reflection instead of compiled delegates or source generators."},{"method":"System.Text.UnicodeEncoding.GetCharCount + GetChars","inclusivePct":0.9,"exclusivePct":0.9,"callChain":["TdsParser.TryReadSqlStringValue","TryReadPlpUnicodeCharsChunk","UnicodeEncoding.GetCharCount / GetChars"],"observation":"Unicode string decoding from SQL wire protocol (GetCharCount 788, GetChars 601, CreateStringFromEncoding 195) totals ~1584 samples. This is a direct consequence of reading large/many NVARCHAR columns. Projecting only needed columns and limiting string field lengths would reduce this proportionally."},{"method":"Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync","inclusivePct":8.5,"exclusivePct":0.1,"callChain":["Controller action","ToListAsync","SingleQueryingEnumerable.MoveNextAsync","ExecuteReaderAsync"],"observation":"ToListAsync (206 exclusive but very high inclusive) materializes entire result sets into memory. Combined with the 11% error rate and 453ms p95 latency, this suggests unbounded queries returning too many rows. Adding pagination (Take/Skip), server-side filtering, or AsNoTracking could dramatically reduce both CPU and memory pressure."}],"summary":"The CPU profile is dominated by SQL data reading (~13500 samples across SqlClient methods) driven by EF Core query materialization, indicating the API fetches too many rows or columns per request — likely missing pagination, projection, or filtering. JSON serialization (~3700 samples) is the second major cost center, amplified by large response payloads. Unusual SortedDictionary usage and runtime reflection on the hot path suggest suboptimal data structure choices and missing compile-time optimizations. The most actionable fixes are: (1) add server-side pagination and column projection to EF Core queries, (2) use lightweight DTOs instead of full entities for serialization, and (3) replace SortedDictionary with a sort-on-demand pattern."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":1.13,"gen1Rate":1.04,"gen2Rate":0.05,"pauseTimeMs":{"avg":6.3,"max":40.5,"total":1638.4},"gcPauseRatio":1.4,"fragmentationPct":0.0,"observations":["Gen1 count (122) is nearly equal to Gen0 count (132) — 92% promotion rate indicates a classic 'mid-life crisis' pattern where objects survive Gen0 but die shortly after promotion to Gen1, wasting GC effort on two generations","Gen0 average pause of 7.5ms and max of 40.5ms are elevated for a workload at 1420 RPS; these pauses overlap with request processing and contribute to tail latency","Gen2 collections are very low (6 total, avg 2.2ms) — long-lived object management is healthy and LOH pressure is minimal","GC pause ratio of 1.4% is within acceptable range (under 5%), so GC is not the dominant latency bottleneck, but the 40.5ms max pause directly impacts p95","Total GC pause of 1638ms across ~117s means roughly 1 in 70 requests could be delayed by a GC pause, correlating with the elevated p95 of 453ms"]},"heapAnalysis":{"peakSizeMB":1328.71,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 1328 MB is very large for an API workload — suggests either large response buffering, uncontrolled caching, or objects being held by async state machines longer than necessary","Total allocation volume is 67,855 MB (~580 MB/sec) across the test — this is an extraordinary allocation rate that drives the high Gen0/Gen1 collection frequency","Zero fragmentation is positive — LOH compaction and pinning are not contributing to memory issues","With 67 GB allocated but only 1.3 GB peak heap, the vast majority of allocations are short-to-mid-lived objects being churned rapidly through Gen0 and Gen1"]},"topAllocators":[{"type":"(allocation data not captured)","allocMB":null,"pctOfTotal":null,"callSite":"unknown","observation":"Allocation tick sampling returned no type data. Re-run PerfView with /DotNetAllocSampled or /DotNetAlloc to capture per-type allocation stacks. At 580 MB/sec, identifying the top allocating types is critical for targeted optimization."}],"summary":"The dominant memory issue is a mid-life crisis pattern: 92% of Gen0 collections promote objects into Gen1, meaning most allocations survive just long enough to escape Gen0 — likely due to async/await state machines, LINQ materializations, or EF Core change-tracker snapshots holding references across await boundaries. At 580 MB/sec allocation rate and 67 GB total volume, the #1 priority is reducing allocation volume in the hot request path — look for repeated large string or byte[] allocations (response serialization, query result buffering), unnecessary LINQ .ToList() materializations, and EF Core AsNoTracking opportunities. Reducing mid-life allocations will collapse Gen1 collection frequency and lower both average and tail latency. The 11.11% error rate may indicate thread pool starvation or memory-related timeouts under this allocation pressure."}
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

Respond with JSON only. No markdown, no code blocks around the JSON.
