Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 3)
- p95 Latency: 1641.868ms
- Requests/sec: 533.7
- Error rate: 11.11%
- Improvement vs baseline: 20.1%

## Baseline Performance
- p95 Latency: 2054.749925ms
- Requests/sec: 427.3
- Error rate: 11.11%

## Runtime Counters
- CPU avg: 41.48%
- GC heap max: 1765MB
- Gen2 collections: 1
- Thread pool max threads: 55

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


## Known Optimization Queue
- [TRIED] `SampleApi/Pages/Products/Detail.cshtml.cs` — Product detail page loads entire Reviews and Products tables *(experiment 3 — improved)*
- [TRIED] `SampleApi/Pages/Index.cshtml.cs` — Home page loads all products and reviews for sampling *(experiment 1 — improved)*
- [TRIED] `SampleApi/Controllers/OrdersController.cs` — CreateOrder has N+1 product lookups and double SaveChanges *(experiment 2 — regressed)*

## Last Experiment's Fix
Home page loads all products and reviews for sampling

## Experiment History (with metrics)
Do NOT re-attempt optimizations that were already tried and resulted in stale or regressed outcomes. Propose different targets or approaches instead.
| Exp | File | Outcome | p95 (ms) | RPS | Branch |
|-----|------|---------|----------|-----|--------|
| 1 | — | improved | 1641.9 | 533.7 | hone/experiment-1 |
| 2 | — | build_failure | N/A | N/A | hone/experiment-2 |


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"SampleApi.Pages.Orders.IndexModel.<OnGetAsync>b__0","inclusivePct":45.2,"exclusivePct":0.12,"callChain":["Kestrel RequestHandler","IndexModel.OnGetAsync","EF Core ToListAsync","Lambda b__0 (per-row projection/filtering)"],"observation":"This is the application entry point driving all downstream CPU work. Low exclusive % but extremely high inclusive % — the lambda executes per-row on a large result set, triggering all SQL reading, EF tracking, and collection operations below. The query likely fetches far too many rows."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.TryReadColumnInternal","inclusivePct":5.1,"exclusivePct":0.79,"callChain":["IndexModel.OnGetAsync","ToListAsync","SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TryReadColumnInternal"],"observation":"High sample count for column-level reading indicates the query returns a very large number of rows and/or columns. Combined with TryReadChar (3520), TdsParser.TryRun (1772), TryReadSqlValue (1627), and TryReadSqlValueInternal (1598), SQL data deserialization consumes ~5% of total CPU. This is consistent with fetching an unbounded result set (missing TOP/pagination)."},{"method":"System.Collections.Generic.SortedDictionary/SortedSet enumeration cluster","inclusivePct":2.8,"exclusivePct":2.8,"callChain":["IndexModel.OnGetAsync","EF Core materialization","SortedDictionary.ValueCollection.Enumerator.MoveNext","SortedSet.Enumerator.MoveNext"],"observation":"SortedDictionary and SortedSet enumeration accounts for ~17,000 samples across MoveNext (3041+2585+2043), GetEnumerator (1304+1275+732), Initialize (1252), get_Current (1426+926), constructor (964+731), and Dispose (708). This is unusual for a web API — suggests in-memory sorting of a large collection, possibly an O(n log n) operation that should be pushed to SQL ORDER BY or eliminated."},{"method":"System.Collections.Generic.Dictionary`2.FindValue","inclusivePct":1.1,"exclusivePct":1.1,"callChain":["EF Core StateManager / IdentityMap","Dictionary<TKey,TValue>.FindValue"],"observation":"5068 samples on Dictionary<Canon,Canon>.FindValue plus 1509 on Dictionary<Int32,Canon>.FindValue and 1464+805 on TryInsert. This is EF Core's identity map performing lookups for every materialized entity. The volume indicates thousands of entities being tracked per request — a strong signal to use AsNoTracking() for read-only queries."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable.MoveNextAsync","inclusivePct":8.5,"exclusivePct":0.52,"callChain":["IndexModel.OnGetAsync","ToListAsync","SingleQueryingEnumerable.MoveNextAsync"],"observation":"3135 exclusive samples in the EF Core async enumeration loop. High inclusive % because this is the pump that drives SqlDataReader, materialization, and change tracking for every row. The sheer iteration count confirms a large unbounded query."},{"method":"Microsoft.EntityFrameworkCore.ChangeTracking.Internal.StateManager.StartTrackingFromQuery","inclusivePct":1.8,"exclusivePct":0.2,"callChain":["SingleQueryingEnumerable.MoveNextAsync","StateManager.StartTrackingFromQuery","IdentityMap.Add","NavigationFixer.InitialFixup"],"observation":"1173 samples plus downstream IdentityMap.Add (1031), NavigationFixer.InitialFixup (1145), InternalEntityEntry constructor (833), and EntityReferenceMap.Update (775). Change tracking machinery totals ~5000 samples. For a read-only page listing orders, this is entirely wasted CPU — AsNoTracking() would eliminate it."},{"method":"System.Runtime.CompilerServices.CastHelpers (ChkCast/IsInstance)","inclusivePct":1.95,"exclusivePct":1.95,"callChain":["EF Core materialization pipeline","CastHelpers.ChkCastAny / ChkCastInterface / IsInstanceOfInterface"],"observation":"~11,685 combined samples across ChkCastAny (4824), ChkCastInterface (4471), IsInstanceOfInterface (1225), IsInstanceOfClass (1165). Type-checking overhead from EF Core's generic materialization pipeline processing thousands of entities. Reducing entity count and using AsNoTracking will reduce this proportionally."},{"method":"Microsoft.EntityFrameworkCore.Diagnostics.Internal.CoreResources.LogStartedTracking","inclusivePct":0.17,"exclusivePct":0.17,"callChain":["StateManager.StartTrackingFromQuery","CoreResources.LogStartedTracking"],"observation":"1018 samples on tracking diagnostics logging, plus NeedsEventData (873) and MessageLogger.IsEnabled (871). EF Core's diagnostic event pipeline fires per-entity. This is pure overhead for a read-only scenario — disabling detailed tracking logs or switching to AsNoTracking eliminates it."},{"method":"System.Text.UnicodeEncoding.GetCharCount / GetChars","inclusivePct":0.49,"exclusivePct":0.49,"callChain":["SqlDataReader.TryReadColumnInternal","TdsParser.TryReadSqlValue","UnicodeEncoding.GetCharCount / GetChars"],"observation":"1556 + 1379 samples on Unicode string decoding from SQL Server. This indicates a large volume of nvarchar/ntext column data being read — consistent with fetching all columns (SELECT *) including large text fields from many rows."},{"method":"System.Text.Json.Serialization.Converters.StringConverter.Write","inclusivePct":0.21,"exclusivePct":0.21,"callChain":["Kestrel response pipeline","JsonSerializer.Serialize","StringConverter.Write"],"observation":"1265 samples on JSON string serialization. The response payload is large enough to show up in CPU profiling, confirming that a massive amount of data is being serialized and sent to the client. Pagination would drastically reduce serialization cost."}],"summary":"The CPU profile is dominated by an unbounded query in Orders.IndexModel.OnGetAsync that fetches a large number of order entities with full EF Core change tracking enabled. Roughly 30% of application-level CPU is spent in SQL data reading (SqlClient TDS parsing), 15% in EF Core change tracking (StateManager, IdentityMap, NavigationFixer), and 10% in SortedDictionary/SortedSet enumeration suggesting expensive in-memory collection processing. The developer should: (1) add pagination or TOP N to the orders query, (2) use AsNoTracking() since this is a read-only page, (3) investigate and eliminate the SortedDictionary usage in favor of SQL-side ordering, and (4) select only needed columns instead of full entities. These changes should dramatically reduce the 1642ms p95 latency and 11% error rate."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.34,"gen1Rate":0.6,"gen2Rate":1.84,"pauseTimeMs":{"avg":37.6,"max":236.3,"total":12552.2},"gcPauseRatio":10.4,"fragmentationPct":0.0,"observations":["GC generation ratio is severely inverted: Gen2 (221) >> Gen1 (72) >> Gen0 (41). Normally Gen0 far exceeds Gen2. This means most allocations are either very large (>85KB, going directly to LOH/Gen2) or objects are being promoted to Gen2 extremely rapidly due to sustained memory pressure.","GC pause ratio of 10.4% is critically high — the application spends over 1 in 10 seconds paused for garbage collection. This directly degrades throughput and inflates tail latency.","Max GC pause of 236.3ms (Gen1) alone accounts for a significant chunk of the 1641ms p95 latency. Multiple GC pauses can stack within a single request lifetime, compounding the effect.","Gen1 collections have the highest average pause time (60.2ms) and highest max pause (236.3ms), suggesting large volumes of data are being promoted from Gen0 to Gen1 and then compacted.","Total allocation volume of ~176 GB over the test run (~1,472 MB/sec) is extremely high, indicating the application is allocating and discarding massive amounts of memory per request."]},"heapAnalysis":{"peakSizeMB":2129.96,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak managed heap of 2.1 GB is very large for an API service — this suggests either large response payloads being buffered in memory, unbounded caching, or large intermediate collections (List<T>, arrays) built per-request.","With 176 GB total allocated and a 2.1 GB peak, the heap turnover ratio is ~83x, meaning the entire heap equivalent is allocated and collected roughly 83 times during the test. This extreme churn is the root cause of the high GC pressure.","Zero fragmentation suggests the GC is compacting successfully, but the cost of compacting a 2+ GB heap is reflected in the high pause times."]},"topAllocators":[{"type":"(allocation type data unavailable)","allocMB":null,"pctOfTotal":null,"callSite":"unknown — allocation tick sampling was not captured or topTypes is empty","observation":"Re-run PerfView with /DotNetAllocSampled and export 'GC Heap Alloc Stacks' to identify which types and call sites drive the 1,472 MB/sec allocation rate. Given the inverted generation ratio, suspect large byte[] or string allocations (>85KB) bypassing Gen0 and landing directly on LOH, or large List<T>/array resizing in hot paths such as query result materialization."}],"summary":"The application is in a severe GC-thrashing state: 10.4% of execution time is spent in GC pauses, driven by ~1,472 MB/sec of allocations that inflate the heap to 2.1 GB peak. The inverted generation distribution (Gen2 collections outnumbering Gen0 5:1) strongly suggests large object allocations hitting LOH or extreme promotion pressure from sustained allocation rates. The #1 priority is to identify and eliminate the largest per-request allocations — likely large result-set materialization (e.g., loading entire database tables into List<T>), unbounded string building, or response serialization buffers. Adding server-side pagination, streaming results with IAsyncEnumerable, and pooling large buffers with ArrayPool<T> would dramatically reduce allocation volume and GC overhead. The 11.11% error rate may be caused by GC-induced timeouts or out-of-memory conditions under load."}
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
