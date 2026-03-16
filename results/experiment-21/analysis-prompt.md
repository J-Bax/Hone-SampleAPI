Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 21)
- p95 Latency: 544.804015ms
- Requests/sec: 1078.1
- Error rate: 0%
- Improvement vs baseline: 92.8%

## Baseline Performance
- p95 Latency: 7546.103045ms
- Requests/sec: 125.5
- Error rate: 0%

## Runtime Counters
- CPU avg: 14.99%
- GC heap max: 1106MB
- Gen2 collections: 35984088
- Thread pool max threads: 35

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


## Known Optimization Queue
- [TRIED] `SampleApi/Controllers/ProductsController.cs` — Add pagination and DTO projection to product list endpoints *(experiment 18 — regressed)*
- [TRIED] `SampleApi/Controllers/ReviewsController.cs` — Eliminate redundant product existence DB round trips in review endpoints *(experiment 19 — improved)*
- [TRIED] `SampleApi/Controllers/OrdersController.cs` — Add AsNoTracking and server-side filtering to GetOrder with batched product lookup *(experiment 20 — improved)*

## Last Experiment's Fix
Add AsNoTracking and server-side filtering to GetOrder with batched product lookup

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


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":1.1,"exclusivePct":1.1,"callChain":["EF Core QueryingEnumerable.MoveNextAsync","TdsParser.TryRun","TdsParser.TryReadSqlStringValue","TdsParserStateObject.TryReadChar"],"observation":"Highest exclusive-time SQL method — character-by-character reading of Unicode string columns from large result sets. Suggests queries return excessive string/nvarchar data; consider projecting only needed columns or reducing result set size with pagination."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1+AsyncEnumerator.MoveNextAsync","inclusivePct":3.8,"exclusivePct":0.16,"callChain":["ToListAsync","SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun"],"observation":"High inclusive % as the main EF Core row-iteration entry point. Nearly all SQL data-reading cost flows through here. High inclusive with low exclusive means the bottleneck is volume of data being read, not the method itself — likely loading too many rows or too many columns per query."},{"method":"Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync","inclusivePct":4.0,"exclusivePct":0.06,"callChain":["Controller Action","ToListAsync","SingleQueryingEnumerable.MoveNextAsync"],"observation":"Call-site that materializes entire query results into memory. If called without .Take() or pagination, it loads all matching rows. This is the most actionable optimization point — add server-side pagination, filtering, or use .Select() projections to reduce data transfer."},{"method":"System.Text.Json.Serialization.Converters.StringConverter.Write","inclusivePct":0.56,"exclusivePct":0.56,"callChain":["JsonSerializer.SerializeAsync","ObjectDefaultConverter.OnTryWrite","JsonPropertyInfo.GetMemberAndWriteJson","StringConverter.Write"],"observation":"Dominant JSON serialization hotspot — writing string properties to the response. Combined with other System.Text.Json frames (~1.4% total), indicates large response payloads with many string fields. Reduce serialized properties with [JsonIgnore] or DTO projections."},{"method":"System.Text.Json (aggregate: ObjectDefaultConverter, JsonWriterHelper, TextEncoder, WriteStack, JsonPropertyInfo)","inclusivePct":1.4,"exclusivePct":0.72,"callChain":["Kestrel HttpProtocol","JsonSerializer.SerializeAsync","ObjectDefaultConverter.OnTryWrite","JsonPropertyInfo.GetMemberAndWriteJson"],"observation":"Aggregate JSON serialization cost is significant. The combination of large result sets from EF Core being fully serialized amplifies this cost. Reducing the number of entities or properties serialized would cut both SQL read and JSON write time."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.PrepareAsyncInvocation","inclusivePct":0.26,"exclusivePct":0.26,"callChain":["SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","PrepareAsyncInvocation"],"observation":"Called once per row read with snapshot save/restore overhead (StateSnapshot.Snap + Clear + PushBuffer total ~0.38%). Per-row async bookkeeping cost scales linearly with row count — another signal that reducing result set size would have compounding benefits."},{"method":"Microsoft.Data.SqlClient.TdsParser.TryReadPlpUnicodeCharsChunk","inclusivePct":0.22,"exclusivePct":0.22,"callChain":["TdsParser.TryRun","TdsParser.TryReadSqlValue","TdsParser.TryReadPlpUnicodeChars","TryReadPlpUnicodeCharsChunk"],"observation":"Reading large PLP (Partially Length-Prefixed) Unicode strings — indicates nvarchar(max) or large text columns being transferred. Consider whether all text columns need to be fetched, or if column sizes can be constrained."},{"method":"System.Runtime.CompilerServices.CastHelpers (aggregate: IsInstanceOfInterface, IsInstanceOfClass, ChkCast*, StelemRef)","inclusivePct":1.65,"exclusivePct":1.65,"callChain":["Various EF Core and DI paths","CastHelpers.IsInstanceOfInterface/IsInstanceOfClass"],"observation":"Type-casting overhead at 1.65% reflects heavy polymorphism in EF Core materialization and DI resolution. Not directly actionable but correlates with entity count — fewer materialized entities means fewer casts."},{"method":"Microsoft.Extensions.DependencyInjection.ResolveService + Dictionary.FindValue(ServiceCacheKey)","inclusivePct":0.3,"exclusivePct":0.3,"callChain":["Controller constructor","ResolveService","Dictionary.FindValue(ServiceCacheKey)"],"observation":"DI service resolution appearing in CPU profile suggests either many transient services resolved per request or deep dependency graphs. Consider switching hot-path services to Singleton or Scoped lifetime if not already."},{"method":"System.Threading.SemaphoreSlim.Wait","inclusivePct":0.06,"exclusivePct":0.06,"callChain":["Async pipeline","SemaphoreSlim.Wait"],"observation":"Synchronous Wait on SemaphoreSlim indicates a sync-over-async code path. Small sample count but a potential thread-pool starvation risk under higher load. Verify no .Result or .Wait() calls exist in the request pipeline."}],"summary":"The CPU profile is dominated by SQL data reading (~3.8% inclusive in EF Core row iteration) and TDS wire-protocol parsing, with JSON serialization adding another ~1.4%. The pattern strongly suggests the API returns large, unpaginated result sets with many string columns — the combination of high TdsParserStateObject.TryReadChar, PLP Unicode chunk reading, and StringConverter.Write all point to excessive data volume flowing from SQL Server through EF Core materialization to JSON serialization. The highest-impact optimization would be adding server-side pagination (.Skip/.Take) or query projections (.Select) to reduce both the number of rows and columns transferred. Secondary gains can come from DTO-based serialization to avoid serializing unnecessary properties."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.88,"gen1Rate":0.76,"gen2Rate":0.034,"pauseTimeMs":{"avg":7.7,"max":160.1,"total":1547.5},"gcPauseRatio":1.3,"fragmentationPct":0.0,"observations":["Gen1/Gen0 ratio is 86.7% (91/105) — an extremely high promotion rate indicating a 'mid-life crisis' pattern where objects survive Gen0 but die shortly after in Gen1, forcing expensive Gen1 collections","Gen1 max pause of 160.1ms directly impacts tail latency — this single pause accounts for ~29% of the observed 544ms p95 latency","Gen0 max pause of 130.4ms is also abnormally high for a Gen0 collection, suggesting the GC is scanning large survivor sets during ephemeral collections","Gen2 collections are rare (4 total) with sub-3ms pauses — long-lived object management is healthy and LOH pressure is minimal","Overall GC pause ratio of 1.3% is acceptable in aggregate, but the max pause spikes (130-160ms) are the real latency killers for p95/p99"]},"heapAnalysis":{"peakSizeMB":1089.29,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 1089MB with 50,266MB total allocated implies ~422 MB/sec allocation rate and massive object churn — objects are being created and discarded at extreme volume","The ratio of total allocations to peak heap (~46:1) confirms very high throughput of short-to-mid-lived objects rather than a memory leak","Zero fragmentation is expected given the low Gen2/LOH activity — the GC is compacting effectively at the ephemeral level"]},"topAllocators":[{"type":"(no allocation tick data available)","allocMB":null,"pctOfTotal":null,"callSite":"unknown","observation":"Allocation tick sampling was not captured or yielded no results. Re-run PerfView with /DotNetAllocSampled and export the 'GC Heap Alloc Stacks' view to identify the specific types and call sites responsible for the ~422 MB/sec allocation rate. Without this data, optimizations must be guided by the GC generation patterns alone."}],"summary":"The dominant issue is a classic mid-life crisis: 86.7% of Gen0 survivors are promoted to Gen1, causing near-equal Gen0 and Gen1 collection counts and Gen1 pause spikes up to 160ms that directly inflate p95 latency. At ~422 MB/sec allocation rate with 50GB total churn, the application is creating massive volumes of objects that live just long enough to escape Gen0. The #1 fix is to identify and eliminate mid-lived allocations — likely per-request buffers, intermediate collections, or EF Core materialization overhead — by introducing object pooling (ArrayPool<T>, ObjectPool<T>), caching query results, and reducing LINQ materialization in hot paths. Re-collect with allocation tick sampling enabled to pinpoint the exact types and call sites driving this churn."}
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
