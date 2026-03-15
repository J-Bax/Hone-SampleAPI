Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 14)
- p95 Latency: 473.982485ms
- Requests/sec: 1325.1
- Error rate: 11.11%
- Improvement vs baseline: 76.9%

## Baseline Performance
- p95 Latency: 2054.749925ms
- Requests/sec: 427.3
- Error rate: 11.11%

## Runtime Counters
- CPU avg: 20.55%
- GC heap max: 1542MB
- Gen2 collections: 32018968
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


## Known Optimization Queue
- [TRIED] `SampleApi/Pages/Products/Index.cshtml.cs` — Products page tracks 1000 entities needlessly on read-only render *(experiment 11 — improved)*
- [TRIED] `SampleApi/Controllers/OrdersController.cs` — OrdersController has N+1 queries and full table scans across multiple endpoints *(experiment 12 — improved)*
- [TRIED] `SampleApi/Pages/Products/Detail.cshtml.cs` — Product detail page uses tracked queries for read-only rendering *(experiment 13 — improved)*

## Last Experiment's Fix
Product detail page uses tracked queries for read-only rendering

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


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":1.72,"exclusivePct":1.72,"callChain":["SingleQueryingEnumerable.MoveNextAsync","TdsParser.TryRun","TdsParser.TryReadSqlValue","TdsParserStateObject.TryReadChar"],"observation":"Highest exclusive-time application-layer method. Reading character data one-char-at-a-time dominates CPU, indicating the query returns very large string/nvarchar columns or an excessive number of rows. Consider projecting fewer columns, reducing result set size, or using streaming."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1.MoveNextAsync","inclusivePct":55.0,"exclusivePct":0.26,"callChain":["ToListAsync","SingleQueryingEnumerable.AsyncEnumerator.MoveNextAsync","TdsParser.TryRun","...SqlDataReader.TryReadColumnInternal"],"observation":"Very high inclusive % (drives the entire SQL read pipeline) but minimal exclusive time — this is the top-level call site that pulls all SQL data. Combined with ToListAsync, indicates full materialization of large result sets into memory. Use pagination, server-side filtering, or Select projections to reduce data volume."},{"method":"System.Text.Json.Serialization.Converters.StringConverter.Write","inclusivePct":0.7,"exclusivePct":0.7,"callChain":["ObjectDefaultConverter.OnTryWrite","GetMemberAndWriteJson","JsonConverter.TryWrite","StringConverter.Write"],"observation":"JSON string serialization is the single most expensive serialization method. Large numbers of string properties being serialized confirms the API returns oversized payloads. Consider returning fewer fields or implementing pagination to shrink response bodies."},{"method":"System.Collections.Generic.SortedDictionary`2+ValueCollection+Enumerator.MoveNext / SortedSet`1+Enumerator.MoveNext","inclusivePct":0.8,"exclusivePct":0.8,"callChain":["...Application Code or EF Materialization","SortedDictionary.ValueCollection.GetEnumerator","SortedSet.Enumerator.MoveNext"],"observation":"SortedDictionary/SortedSet enumeration accounts for ~4,900 samples. These O(log n) tree-based collections are expensive to enumerate vs Dictionary/List. If ordering is only needed at output time, switch to Dictionary and sort once at the end, or use a List with a single Sort call."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.PrepareAsyncInvocation","inclusivePct":0.43,"exclusivePct":0.43,"callChain":["SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","SqlDataReader.PrepareAsyncInvocation"],"observation":"Async invocation prep is called per-row. Combined with StateSnapshot.Snap (1545), SetSnapshot (824), and StateSnapshot.Clear (937), snapshot management adds ~6,000 samples of per-row overhead. Reducing row count is the most effective mitigation."},{"method":"System.Runtime.CompilerServices.CastHelpers (IsInstanceOfInterface, IsInstanceOfClass, ChkCastAny, ChkCastInterface)","inclusivePct":2.31,"exclusivePct":2.31,"callChain":["Various call sites","CastHelpers.IsInstanceOfInterface / ChkCastAny"],"observation":"Type-casting helpers consume ~14,200 samples combined. This is unusually high and suggests heavy use of polymorphic interfaces, object-typed collections, or boxing. Often correlates with EF Core materialization of untyped result sets. Using strongly-typed projections reduces cast overhead."},{"method":"Microsoft.Data.SqlClient.TdsParser.TryReadPlpUnicodeCharsChunk","inclusivePct":0.33,"exclusivePct":0.33,"callChain":["TryRun","TryReadSqlValue","TryReadSqlStringValue","TryReadPlpUnicodeCharsChunk"],"observation":"PLP (Partial Length Prefixed) unicode reading indicates nvarchar(max) or large text columns being streamed from SQL. If the API doesn't need full text content, use substring projections in the query or store/return truncated summaries."},{"method":"System.Text.Json.Serialization.Converters.ObjectDefaultConverter`1.OnTryWrite","inclusivePct":1.5,"exclusivePct":0.19,"callChain":["JsonSerializer.SerializeAsync","JsonConverter.TryWrite","ObjectDefaultConverter.OnTryWrite","GetMemberAndWriteJson"],"observation":"High inclusive call-site for all JSON property serialization. The reflection-based default converter is slower than source-generated serializers. Consider using System.Text.Json source generators for the response DTOs to reduce serialization overhead."},{"method":"Microsoft.Extensions.DependencyInjection.ResolveService + ServiceCacheKey.FindValue","inclusivePct":0.28,"exclusivePct":0.28,"callChain":["Controller Action","ResolveService","Dictionary.FindValue(ServiceCacheKey)"],"observation":"DI resolution appears per-request with ~1,690 samples. If scoped services are resolved repeatedly within a request (e.g., inside loops or per-entity), consider caching resolved instances or restructuring to inject once at the controller level."},{"method":"dynamicClass.lambda_method49 (EF Core Materializer)","inclusivePct":0.15,"exclusivePct":0.15,"callChain":["SingleQueryingEnumerable.MoveNextAsync","Shaper/Materializer","lambda_method49"],"observation":"EF Core's dynamically compiled entity materializer. 938 samples of pure object construction from SQL rows. High count confirms a large number of entities being materialized — use .Select() projections to materialize only DTOs with needed fields instead of full entities."}],"summary":"The CPU profile is overwhelmingly dominated by SQL data reading — TDS parsing, string decoding, async state snapshots, and row-by-row materialization collectively account for the majority of application CPU time. This strongly indicates the API fetches too many rows and/or too many columns (especially large nvarchar fields) from the database. The second major cost center is JSON serialization of these oversized result sets. A developer should focus first on adding server-side pagination or filtering to reduce result set size, then use .Select() projections to fetch only needed columns, and consider replacing SortedDictionary with Dictionary where sort order isn't required during construction. The 11.11% error rate likely stems from timeouts or resource exhaustion under this data volume pressure."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":1.09,"gen1Rate":1.03,"gen2Rate":0.05,"pauseTimeMs":{"avg":8.84,"max":119.1,"total":2297.3},"gcPauseRatio":1.9,"fragmentationPct":0.0,"observations":["Gen0 and Gen1 collection counts are nearly identical (131 vs 123), meaning almost every Gen0 collection promotes surviving objects into Gen1 and immediately triggers a Gen1 collection — this 1:1 ratio indicates a large population of mid-lived objects that outlive Gen0 but die shortly after promotion","Max Gen0 pause of 119.1ms is extreme for an ephemeral collection and directly contributes to p95 latency (473ms); this suggests Gen0 collections are promoting large volumes of data, forcing expensive copying","Max Gen1 pause of 79.8ms is also very high, reinforcing that promoted object volume is substantial and Gen1 heap is growing between collections","Gen2 collections are rare (6 total) with low pauses (max 4ms) — long-lived objects are well-managed; the problem is entirely in the ephemeral generations","GC pause ratio of 1.9% is below the 5% alarm threshold in aggregate, but individual max pauses (119ms) are severe tail-latency killers that directly explain the elevated p95"]},"heapAnalysis":{"peakSizeMB":1607.23,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 1.6 GB under load is very large for an API service — this suggests either large object graphs held in memory (caches, result sets) or a high watermark caused by allocation bursts outpacing GC","Total allocation of 87,654 MB (~730 MB/sec over ~120s) is extremely high throughput — the allocator is under massive pressure, creating and discarding objects at enormous rates","Zero fragmentation indicates LOH is not a concern; the problem is purely ephemeral allocation volume and mid-life object promotion"]},"topAllocators":[{"type":"(no allocation type breakdown available)","allocMB":null,"pctOfTotal":null,"callSite":"unknown — allocation tick data was not captured or empty","observation":"Re-run diagnostics with /DotNetAllocSampled and export the 'GC Heap Alloc Stacks' view to identify which types and call sites drive the 730 MB/sec allocation rate. Likely culprits at this volume: large byte[] or string allocations from serialization, EF Core materialization buffers, or unbounded query result sets"}],"summary":"The API is allocating ~730 MB/sec with a 1.6 GB peak heap, and the near-1:1 Gen0-to-Gen1 collection ratio reveals that a large class of objects survives Gen0 only to die in Gen1 — classic mid-life crisis pattern. This forces expensive promotion copying, producing Gen0 pauses up to 119ms that directly inflate p95 latency to 474ms. The 11.11% error rate may stem from thread-pool starvation during these long GC pauses. The #1 priority is to reduce the volume of mid-lived allocations — likely large EF Core result-set materializations or response serialization buffers — by paginating queries, pooling buffers with ArrayPool<T>, and caching repeated query results to cut allocation volume and eliminate the promotion storm."}
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
