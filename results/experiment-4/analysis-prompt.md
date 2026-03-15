Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 4)
- p95 Latency: 2179.352105ms
- Requests/sec: 349.3
- Error rate: 0%
- Improvement vs baseline: 71.1%

## Baseline Performance
- p95 Latency: 7546.103045ms
- Requests/sec: 125.5
- Error rate: 0%

## Runtime Counters
- CPU avg: 64.79%
- GC heap max: 5363MB
- Gen2 collections: 648317968
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


## Known Optimization Queue
- [TRIED] `SampleApi/Controllers/ProductsController.cs` — Eliminate full-table product scans with server-side filtering *(experiment 1 — improved)*
- [TRIED] `SampleApi/Controllers/ReviewsController.cs` — Replace full reviews table scan with server-side query filtering *(experiment 2 — improved)*
- [TRIED] `SampleApi/Pages/Checkout/Index.cshtml.cs` — Eliminate N+1 queries and per-item SaveChanges in checkout flow *(experiment 3 — improved)*

## Last Experiment's Fix
Eliminate N+1 queries and per-item SaveChanges in checkout flow

## Experiment History (with metrics)
Do NOT re-attempt optimizations that were already tried and resulted in stale or regressed outcomes. Propose different targets or approaches instead.
| Exp | File | Outcome | p95 (ms) | RPS | Branch |
|-----|------|---------|----------|-----|--------|
| 1 | — | improved | 2203.8 | 341.5 | hone/experiment-1 |
| 2 | — | improved | 2166.1 | 346.6 | hone/experiment-2 |
| 3 | — | improved | 2179.4 | 349.3 | hone/experiment-3 |


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"SampleApi.Pages.Orders.IndexModel.<OnGetAsync>b__2(OrderItem)","inclusivePct":28.5,"exclusivePct":0.37,"callChain":["IndexModel.OnGetAsync","LINQ lambda over Orders","lambda b__2 per OrderItem"],"observation":"Lambda invoked per OrderItem during order processing in OnGetAsync — the high inclusive cost comes from triggering SortedDictionary enumeration, string processing, and collection manipulation for every item. Suggests in-memory aggregation or grouping of order items that should be done in the database query instead."},{"method":"SampleApi.Pages.Orders.IndexModel.<OnGetAsync>b__0(Order)","inclusivePct":22.0,"exclusivePct":0.1,"callChain":["IndexModel.OnGetAsync","EF Core ToListAsync","lambda b__0 per Order"],"observation":"Per-order processing lambda with low exclusive but high inclusive cost — each invocation triggers nested OrderItem iteration (b__2), SortedDictionary lookups, and entity materialization. Classic N×M in-memory pattern: all orders and their items are loaded then processed client-side."},{"method":"System.Collections.Generic.SortedDictionary/SortedSet enumeration (combined)","inclusivePct":3.2,"exclusivePct":2.6,"callChain":["IndexModel.OnGetAsync","Order/OrderItem lambda","SortedDictionary.Values.GetEnumerator","SortedSet.Enumerator.MoveNext"],"observation":"SortedDictionary is being enumerated heavily (~25,800 samples across MoveNext, GetEnumerator, Initialize, get_Current, Dispose). SortedDictionary has O(n) enumeration with high constant factor due to tree traversal and Stack allocation. Replace with Dictionary if sort order is not required, or sort once at the end instead of maintaining sorted order during construction."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable.MoveNextAsync","inclusivePct":18.5,"exclusivePct":0.64,"callChain":["IndexModel.OnGetAsync","ToListAsync","SingleQueryingEnumerable.AsyncEnumerator.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun"],"observation":"EF Core is materializing a very large result set row-by-row. The high inclusive cost reflects the entire SQL→TDS→materialization→tracking pipeline per row. Consider using .AsNoTracking() to skip change tracking, server-side projection (Select) to reduce columns transferred, and pagination to limit result set size."},{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar / TDS parsing pipeline","inclusivePct":5.8,"exclusivePct":5.2,"callChain":["SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.TryReadColumnInternal","TdsParser.TryRun","TdsParserStateObject.TryReadChar"],"observation":"TDS wire protocol parsing consumes ~52,000 samples across TryReadChar (8,355), TryReadColumnInternal (7,896), TryGetTokenLength (4,216), TryProcessColumnHeaderNoNBC (3,738), TryReadSqlValue (2,833), etc. This volume indicates a massive amount of columnar data being read — likely fetching all columns of all orders and order items instead of projecting only needed fields."},{"method":"System.Text.UnicodeEncoding.GetCharCount / GetChars / String decoding","inclusivePct":1.5,"exclusivePct":1.3,"callChain":["TdsParser.TryReadSqlValue","TdsParserStateObject.TryReadString","UnicodeEncoding.GetCharCount","UnicodeEncoding.GetChars","String.CreateStringFromEncoding"],"observation":"String decoding from SQL data accounts for ~12,900 samples. Large string columns (nvarchar) are being materialized for every row. If full string content is not needed (e.g., descriptions, notes), project only needed columns in the EF query to avoid transferring and decoding unnecessary text data."},{"method":"Microsoft.EntityFrameworkCore.ChangeTracking.Internal.StateManager.StartTrackingFromQuery","inclusivePct":1.8,"exclusivePct":0.19,"callChain":["SingleQueryingEnumerable.MoveNextAsync","StateManager.StartTrackingFromQuery","NavigationFixer.InitialFixup","InternalEntityEntry.MarkShadowPropertiesNotSet"],"observation":"EF Core change tracking overhead for each materialized entity: StartTrackingFromQuery (1,949), NavigationFixer.InitialFixup (1,473), InternalEntityEntry ctor (1,318), MarkShadowPropertiesNotSet (1,385), GetOrCreateIdentityMap (1,212). Since this is a read-only page, use .AsNoTracking() to eliminate ~7,300 samples of tracking overhead."},{"method":"System.Collections.Generic.Dictionary.FindValue / TryInsert","inclusivePct":1.3,"exclusivePct":1.3,"callChain":["StateManager.StartTrackingFromQuery","IdentityMap.FindValue","Dictionary.FindValue/TryInsert"],"observation":"Dictionary operations total ~12,850 samples — primarily driven by EF Core's identity map maintaining tracked entities. FindValue (Canon): 7,100 + FindValue (Int32): 1,919 + TryInsert: 3,838. Eliminating change tracking with AsNoTracking() would remove most of this overhead."},{"method":"System.Runtime.CompilerServices.CastHelpers (ChkCast/IsInstance)","inclusivePct":2.1,"exclusivePct":2.1,"callChain":["Various EF Core materialization paths","CastHelpers.ChkCastInterface/ChkCastAny/IsInstanceOfClass"],"observation":"Type-checking overhead of ~21,300 samples (ChkCastInterface: 8,152, ChkCastAny: 7,460, IsInstanceOfClass: 2,780, IsInstanceOfInterface: 1,735) is unusually high, indicating heavy polymorphic dispatch during entity materialization and collection processing. This is a secondary symptom — reducing the number of entities materialized will proportionally reduce cast overhead."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.ReadAsync / PrepareAsyncInvocation","inclusivePct":12.0,"exclusivePct":0.54,"callChain":["SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","PrepareAsyncInvocation","TdsParser.TryRun"],"observation":"ReadAsync (2,712) + PrepareAsyncInvocation (2,684) show per-row async overhead. Each row incurs execution context capture (1,481), state snapshot (1,765), and timeout management (1,125). With thousands of rows, this async ceremony adds up. Reducing result set size via server-side filtering/pagination is the primary fix."}],"summary":"The CPU profile is dominated by the Orders.IndexModel.OnGetAsync handler loading an excessively large dataset — all orders with all order items — then processing them in-memory using SortedDictionary and per-item lambdas. The three highest-impact optimizations are: (1) Add server-side filtering and pagination to the EF Core query instead of loading all orders, (2) Use .AsNoTracking() since this is a read-only page, eliminating ~15% of overhead from change tracking, identity maps, and navigation fixup, and (3) Replace SortedDictionary with Dictionary or move sorting to the SQL query via OrderBy, eliminating the expensive tree-based collection enumeration (~25K samples). Secondary wins include projecting only needed columns (.Select) to reduce TDS parsing and string decoding overhead."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":5,"gen1Rate":41,"gen2Rate":171,"pauseTimeMs":{"avg":88.9,"max":422.8,"total":19289.7},"gcPauseRatio":15.6,"fragmentationPct":0.0,"observations":["CRITICAL: Inverted GC generation distribution — Gen2 (171) vastly outnumbers Gen1 (41) and Gen0 (5). Normal applications show Gen0 >> Gen1 >> Gen2. This inversion strongly indicates massive Large Object Heap (LOH) allocations (objects >85KB), which bypass Gen0/Gen1 and trigger Gen2 collections directly.","GC pause ratio is 15.6% — the runtime spends nearly 1 in 6 seconds paused for garbage collection. This is 3x the 5% concern threshold and is a primary driver of the 2179ms p95 latency.","Max GC pause of 422.8ms (Gen1) alone accounts for ~19% of the p95 latency target. Gen2 max pause of 302.7ms is also severe. These blocking pauses directly inflate tail latencies.","Gen1 avg pause (157.9ms) exceeds Gen2 avg pause (71.2ms), suggesting Gen1 collections are triggering concurrent or compacting Gen2 collections, or that Gen1 survivors are heavily promoted.","Total 189,429 MB allocated during the test with peak heap at 2.6GB — the allocation rate is extreme and the heap is under constant pressure, forcing continuous full GC cycles.","Low Gen0 count (5) means almost no short-lived small objects are collected cheaply — nearly all allocation pressure is routed through expensive Gen2 collections via the LOH."]},"heapAnalysis":{"peakSizeMB":2596.2,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 2.6GB is extremely large for an API service and indicates the application is holding large buffers, collections, or serialized payloads in memory.","Total allocation of 189,429 MB (~185GB) during the load test means the GC must reclaim memory at an enormous rate — this drives the 171 Gen2 collections.","Zero fragmentation suggests the GC is compacting effectively, but at the cost of blocking pauses during compaction.","The combination of high peak heap and inverted generation counts points to repeated allocation of large arrays, lists, strings, or byte buffers (>85KB each) that land on the LOH."]},"topAllocators":[{"type":"(allocation type data unavailable)","allocMB":null,"pctOfTotal":null,"callSite":"Unknown — no allocation tick data captured","observation":"No per-type allocation data was captured. To identify the specific culprits, re-run PerfView with /DotNetAllocSampled and export GC Heap Alloc Stacks. Based on the inverted GC pattern, the top allocators are almost certainly large arrays (byte[], object[], string[]) or large strings (>85KB) created during request processing — likely from unbounded database query result materialization, large JSON serialization buffers, or unstreamed response bodies."}],"summary":"The application is in severe GC distress: 171 Gen2 collections vastly outnumber Gen0 (5) and Gen1 (41), indicating massive Large Object Heap allocations (objects >85KB) that bypass ephemeral generations entirely. With a 15.6% GC pause ratio and max pauses of 422.8ms, garbage collection is the dominant contributor to the 2179ms p95 latency. The #1 fix is to eliminate LOH allocations in the hot path — look for unbounded query results being materialized into large List<T> or arrays, large string concatenations, and unstreamed serialization. Apply pagination to database queries, use ArrayPool<T>/RecyclableMemoryStream for buffers, and stream responses instead of buffering entire payloads in memory."}
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
