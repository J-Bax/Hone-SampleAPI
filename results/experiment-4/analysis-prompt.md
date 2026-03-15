Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 4)
- p95 Latency: 1526.65415ms
- Requests/sec: 494.4
- Error rate: 11.11%
- Improvement vs baseline: 4.4%

## Baseline Performance
- p95 Latency: 1596.242785ms
- Requests/sec: 468.5
- Error rate: 11.11%

## Runtime Counters
- CPU avg: 65.03%
- GC heap max: 1770MB
- Gen2 collections: 2
- Thread pool max threads: 40

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
| 1 | 2026-03-15 04:10 | `SampleApi/Controllers/ProductsController.cs` | Full table scans with in-memory filtering on Products | improved |
| 2 | 2026-03-15 04:36 | `SampleApi/Controllers/CartController.cs` | N+1 queries, full table scans, and per-item SaveChanges in Cart | improved |
| 3 | 2026-03-15 05:01 | `SampleApi/Controllers/ReviewsController.cs` | Full table scan of ~2000 Reviews with in-memory filtering | improved |


## Known Optimization Queue
- [TRIED] `SampleApi/Controllers/ProductsController.cs` — Full table scans with in-memory filtering on Products *(experiment 1 — improved)*
- [TRIED] `SampleApi/Controllers/CartController.cs` — N+1 queries, full table scans, and per-item SaveChanges in Cart *(experiment 2 — improved)*
- [TRIED] `SampleApi/Controllers/ReviewsController.cs` — Full table scan of ~2000 Reviews with in-memory filtering *(experiment 3 — improved)*

## Last Experiment's Fix
Full table scan of ~2000 Reviews with in-memory filtering

## Experiment History (with metrics)
Do NOT re-attempt optimizations that were already tried and resulted in stale or regressed outcomes. Propose different targets or approaches instead.
| Exp | File | Outcome | p95 (ms) | RPS | Branch |
|-----|------|---------|----------|-----|--------|
| 1 | — | improved | 1557.3 | 474.2 | hone/experiment-1 |
| 2 | — | improved | 1581.7 | 476.3 | hone/experiment-2 |
| 3 | — | improved | 1526.7 | 494.4 | hone/experiment-3 |


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1+AsyncEnumerator.MoveNextAsync","inclusivePct":18.2,"exclusivePct":0.61,"callChain":["Controller Action","ToListAsync","SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun"],"observation":"Primary EF Core query execution loop consuming massive CPU in downstream SQL reading and entity materialization. High inclusive % with low exclusive % indicates this is the gateway to all data access cost — the queries being executed return far too many rows or columns."},{"method":"Microsoft.Data.SqlClient.SqlDataReader (aggregate: TryReadColumnInternal, TryReadChar, ReadAsync, GetInt32, GetString)","inclusivePct":12.5,"exclusivePct":8.25,"callChain":["ToListAsync","MoveNextAsync","TdsParser.TryRun","SqlDataReader.TryReadColumnInternal","TdsParserStateObject.TryReadChar"],"observation":"SqlDataReader methods collectively dominate exclusive CPU time (~77K samples). The volume of TryReadChar and TryReadColumnInternal calls indicates the API is reading an enormous number of rows and columns per request — classic symptom of missing pagination, missing projection (SELECT *), or over-eager Include() loading."},{"method":"Microsoft.EntityFrameworkCore.ChangeTracking.Internal.NavigationFixer.InitialFixup","inclusivePct":5.6,"exclusivePct":0.28,"callChain":["ToListAsync","StateManager.StartTrackingFromQuery","NavigationFixer.InitialFixup","SortedDictionary.GetEnumerator","SortedSet.Enumerator.MoveNext"],"observation":"EF Core is fixing up navigation properties for a very large number of tracked entities. The SortedDictionary/SortedSet operations beneath this (28K+ samples) reveal the identity map is enormous. Adding AsNoTracking() to read-only queries would eliminate this entire cost path."},{"method":"System.Collections.Generic.SortedSet`1+Enumerator.MoveNext (EF Core identity map)","inclusivePct":3.01,"exclusivePct":3.01,"callChain":["NavigationFixer.InitialFixup","SortedDictionary.Values.GetEnumerator","SortedSet.Enumerator.MoveNext"],"observation":"SortedDictionary/SortedSet enumeration accounts for ~28K exclusive samples — the EF Core change tracker's identity map is being iterated extensively. This O(n) enumeration per tracked entity makes change tracking cost grow quadratically with result set size."},{"method":"Microsoft.EntityFrameworkCore.ChangeTracking.Internal.StateManager.StartTrackingFromQuery","inclusivePct":4.2,"exclusivePct":0.25,"callChain":["ToListAsync","MoveNextAsync","StateManager.StartTrackingFromQuery","GetOrCreateIdentityMap","InternalEntityEntry..ctor"],"observation":"Every materialized entity is registered in the change tracker's identity map. Combined with InternalEntityEntry construction (1491 samples) and GetOrCreateIdentityMap (1532 samples), this confirms thousands of entities are being tracked per request. Use AsNoTracking() or projection with Select()."},{"method":"System.Runtime.CompilerServices.CastHelpers (ChkCastInterface, ChkCastAny, IsInstanceOfClass, IsInstanceOfInterface)","inclusivePct":2.33,"exclusivePct":2.33,"callChain":["MoveNextAsync","Entity Materialization","CastHelpers.ChkCastInterface"],"observation":"~22K exclusive samples in type-casting operations driven by EF Core entity materialization and polymorphic dispatch. This is proportional to the number of entities materialized — reducing result set size directly reduces this overhead."},{"method":"System.Text.UnicodeEncoding.GetCharCount / GetChars","inclusivePct":1.64,"exclusivePct":1.64,"callChain":["TdsParser.TryReadSqlStringValue","UnicodeEncoding.GetCharCount","UnicodeEncoding.GetChars","String.CreateStringFromEncoding"],"observation":"~15K exclusive samples in Unicode string decoding from SQL Server. The API is reading large volumes of NVARCHAR string data. Using projection (Select) to fetch only needed columns would reduce both network transfer and string allocation."},{"method":"System.Collections.Generic.Dictionary`2.FindValue / TryInsert","inclusivePct":1.35,"exclusivePct":1.35,"callChain":["StateManager.StartTrackingFromQuery","GetOrCreateIdentityMap","Dictionary.FindValue"],"observation":"~12.5K exclusive samples in Dictionary lookups, primarily from EF Core identity map resolution. High FindValue count confirms many entities are being looked up and inserted during query materialization."},{"method":"Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync","inclusivePct":25.0,"exclusivePct":0.23,"callChain":["Controller Action","ToListAsync"],"observation":"Top-level query materialization call with extremely high inclusive cost. The application is calling ToListAsync() to materialize entire query results into memory. Consider pagination (Skip/Take), streaming with IAsyncEnumerable, or returning only the fields needed via Select() projection."},{"method":"Microsoft.EntityFrameworkCore.Diagnostics.Internal.CoreResources.LogStartedTracking","inclusivePct":0.15,"exclusivePct":0.15,"callChain":["StateManager.StartTrackingFromQuery","CoreResources.LogStartedTracking","Logger.IsEnabled"],"observation":"1422 samples in tracking diagnostics logging plus ~2.6K in Logger.IsEnabled checks. EF Core is evaluating logging for every tracked entity. Consider disabling detailed EF Core logging in production or using LogLevel filtering to reduce this overhead."}],"summary":"The CPU profile is overwhelmingly dominated by data access: SQL result reading (SqlClient ~8.3%), EF Core change tracking and identity map operations (~5.6%), and entity materialization overhead (type casting ~2.3%, string decoding ~1.6%). The pattern strongly indicates the API is fetching far too much data per request — likely full table scans or unfiltered queries materialized via ToListAsync() without pagination, projection, or AsNoTracking(). The three highest-impact optimizations are: (1) add AsNoTracking() to read-only queries to eliminate the ~52K samples spent in change tracking and SortedDictionary enumeration, (2) add Select() projections to fetch only required columns and reduce SQL reading and string allocation, and (3) add pagination (Skip/Take) or filtering to limit result set sizes. The 11.11% error rate likely stems from request timeouts caused by these oversized queries under load."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":4,"gen1Rate":65,"gen2Rate":222,"pauseTimeMs":{"avg":53.7,"max":213.4,"total":15639.3},"gcPauseRatio":13.0,"fragmentationPct":0.0,"observations":["GC generation distribution is severely inverted: 222 Gen2 vs 65 Gen1 vs 4 Gen0. Normal apps show Gen0 >> Gen1 >> Gen2. This indicates massive large-object or long-lived allocations forcing full Gen2 collections.","Gen1 collections have the highest average pause (104.9ms, max 213.4ms) — these are the primary contributor to tail latency and directly explain the 1526ms p95.","Gen2 collections dominate by count (76% of all GCs) with 38.1ms avg pause — individually moderate but their sheer volume accumulates 8,458ms of pause time.","GC pause ratio of 13% is critically high (threshold: 5%). The runtime spends 1 in every 8 milliseconds paused for GC, destroying throughput and inflating latency across all percentiles.","206 GB total allocated during the load test is extreme — this volume of allocation is the root driver of constant GC pressure and explains the 11% error rate (likely timeouts during prolonged GC pauses)."]},"heapAnalysis":{"peakSizeMB":2259.54,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 2.26 GB is dangerously high for an API service — this suggests unbounded caching, large result sets materialized in memory, or LOH bloat from large arrays/strings.","The combination of 2.26 GB peak heap and 206 GB total allocations means the app is churning through ~91x its peak heap size — objects are allocated and discarded at an extraordinary rate.","Zero fragmentation suggests the GC is compacting effectively, but at enormous cost: the 222 Gen2 collections are the price being paid for keeping fragmentation at zero on a huge heap."]},"topAllocators":[{"type":"(allocation type data unavailable)","allocMB":null,"pctOfTotal":null,"callSite":"unknown — allocation sampling was not captured or yielded no type-level breakdown","observation":"Re-run PerfView with /DotNetAllocSampled to capture allocation tick events and identify which types and call sites are responsible for the 206 GB of allocations. Without this data, optimization must be guided by the GC pattern: the inverted Gen0/Gen2 ratio points to large object allocations (>85KB arrays, large strings, or big List<T>/byte[] buffers) as the likely culprits."}],"summary":"The application is in a GC crisis: 13% of execution time is spent in GC pauses, with 222 Gen2 collections indicating massive allocation of large or long-lived objects totaling 206 GB during the test. The #1 priority is to reduce allocation volume — investigate endpoints that materialize large database result sets into memory (e.g., unbounded queries returning thousands of rows as List<T>), large string concatenations, or byte[] buffers for serialization. Adding pagination, using IAsyncEnumerable streaming, pooling buffers with ArrayPool<T>, and caching expensive query results will dramatically reduce GC pressure. The 213ms max GC pause directly explains the p95 latency, and the 11% error rate is almost certainly caused by request timeouts during sustained GC pauses."}
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
