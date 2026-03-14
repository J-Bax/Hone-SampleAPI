Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 6)
- p95 Latency: 482.42257ms
- Requests/sec: 1321.2
- Error rate: 11.11%
- Improvement vs baseline: 76.5%

## Baseline Performance
- p95 Latency: 2054.749925ms
- Requests/sec: 427.3
- Error rate: 11.11%

## Runtime Counters
- CPU avg: 21.47%
- GC heap max: 2093MB
- Gen2 collections: 0
- Thread pool max threads: 37

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


## Known Optimization Queue
- [TRIED] `SampleApi/Pages/Orders/Index.cshtml.cs` — Orders page loads entire Orders and OrderItems tables with N+1 product lookups *(experiment 3 — improved)*
- [TRIED] `SampleApi/Pages/Checkout/Index.cshtml.cs` — Checkout page has per-item SaveChanges, full table scans, and N+1 queries *(experiment 4 — improved)*
- [TRIED] `SampleApi/Pages/Cart/Index.cshtml.cs` — Cart page loads entire CartItems table and has N+1 product lookups *(experiment 5 — improved)*

## Last Experiment's Fix
Cart page loads entire CartItems table and has N+1 product lookups

## Experiment History (with metrics)
Do NOT re-attempt optimizations that were already tried and resulted in stale or regressed outcomes. Propose different targets or approaches instead.
| Exp | File | Outcome | p95 (ms) | RPS | Branch |
|-----|------|---------|----------|-----|--------|
| 1 | — | improved | 1641.9 | 533.7 | hone/experiment-1 |
| 2 | — | build_failure | N/A | N/A | hone/experiment-2 |
| 3 | — | improved | 480.1 | 1352.2 | hone/experiment-3 |
| 4 | — | improved | 481.5 | 1323.9 | hone/experiment-4 |
| 5 | — | improved | 482.4 | 1321.2 | hone/experiment-5 |


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":2.9,"exclusivePct":2.9,"callChain":["SqlDataReader.ReadAsync","TdsParser.TryRun","TdsParser.TryReadSqlValue","TdsParserStateObject.TryReadChar"],"observation":"Highest exclusive-time application frame. Reading character data from TDS wire protocol dominates CPU — indicates the query is returning a very large volume of string/nvarchar columns, likely fetching far more rows or wider rows than necessary."},{"method":"Microsoft.Data.SqlClient.* (SQL Data Access aggregate)","inclusivePct":10.3,"exclusivePct":10.3,"callChain":["EF Core SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun","TdsParser.TryRead*"],"observation":"Aggregating all TdsParser, SqlDataReader, SqlBuffer, and StateSnapshot methods yields ~31K samples (10.3%). The query is returning massive result sets — TryReadPlpUnicodeCharsChunk, TryReadSqlStringValue, and UnicodeEncoding.GetChars all point to bulk string-column reading. Classic N+1 or SELECT * anti-pattern."},{"method":"System.Collections.Generic.SortedDictionary/SortedSet enumeration","inclusivePct":2.3,"exclusivePct":2.3,"callChain":["Application code or EF navigation","SortedDictionary.ValueCollection.GetEnumerator","SortedSet.Enumerator.MoveNext"],"observation":"~6,900 samples across SortedDictionary/SortedSet enumeration. SortedSet uses a tree with O(log n) traversal and allocates a Stack on each enumeration (Stack.ctor: 439 samples). If ordering isn't required, replacing with Dictionary/HashSet would eliminate tree-walk and allocation overhead."},{"method":"System.Text.Json.Serialization (JSON serialization aggregate)","inclusivePct":2.3,"exclusivePct":2.3,"callChain":["ASP.NET response pipeline","ObjectDefaultConverter.OnTryWrite","JsonPropertyInfo.GetMemberAndWriteJson","StringConverter.Write"],"observation":"~7,000 samples in JSON serialization with StringConverter.Write (3,353) dominating. This confirms large response payloads with many string properties. Reducing the data returned (pagination, projection, DTOs with fewer fields) would reduce both SQL read and serialization cost."},{"method":"System.Runtime.CompilerServices.CastHelpers (type casting aggregate)","inclusivePct":4.1,"exclusivePct":4.1,"callChain":["EF Core materialization / DI resolution","CastHelpers.ChkCastAny / IsInstanceOfInterface / IsInstanceOfClass"],"observation":"~12,500 samples in runtime type-checking (ChkCast, IsInstanceOf). This is driven by EF Core entity materialization boxing/unboxing and generic interface dispatch. Not directly fixable but proportional to number of entities materialized — reducing result set size will reduce this proportionally."},{"method":"Microsoft.EntityFrameworkCore.ChangeTracking.Internal.StateManager.StartTrackingFromQuery","inclusivePct":1.8,"exclusivePct":0.16,"callChain":["SingleQueryingEnumerable.MoveNextAsync","StateManager.StartTrackingFromQuery","NavigationFixer.InitialFixup","InternalEntityEntry..ctor"],"observation":"EF Core is tracking every entity returned from queries. With StartTrackingFromQuery (482), NavigationFixer.InitialFixup (427), InternalEntityEntry..ctor (426), and GetOrCreateIdentityMap (372) totaling ~1,700 samples for change tracking alone. Use .AsNoTracking() for read-only queries to eliminate this overhead entirely."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable.MoveNextAsync","inclusivePct":0.44,"exclusivePct":0.44,"callChain":["ToListAsync","SingleQueryingEnumerable.AsyncEnumerator.MoveNextAsync"],"observation":"1,345 samples in the EF query enumeration loop. Combined with ToListAsync (597), this is the main query execution path. The high sample count confirms queries are returning large result sets that require many MoveNext iterations — add pagination (Take/Skip) or server-side filtering."},{"method":"System.Text.UnicodeEncoding.GetCharCount / GetChars","inclusivePct":1.2,"exclusivePct":1.2,"callChain":["TdsParser.TryReadSqlStringValue","UnicodeEncoding.GetCharCount","UnicodeEncoding.GetChars","String.CreateStringFromEncoding"],"observation":"~4,100 samples decoding Unicode strings from SQL Server wire protocol. This is a downstream effect of reading many/large nvarchar columns. Selecting only needed columns (projection) and reducing row count will reduce this proportionally."},{"method":"System.Threading.ExecutionContext.Capture / SetLocalValue","inclusivePct":1.3,"exclusivePct":1.3,"callChain":["AsyncMethodBuilderCore.Start","ExecutionContext.Capture","ExecutionContext.SetLocalValue"],"observation":"~3,900 samples in async execution context management. Each async state machine transition captures ExecutionContext. This is proportional to the number of async operations — reducing the number of database round-trips (batching, eager loading) would reduce async overhead."},{"method":"Microsoft.Extensions.DependencyInjection.ResolveService + Dictionary lookups","inclusivePct":0.42,"exclusivePct":0.42,"callChain":["Request pipeline","DI ResolveService","Dictionary.FindValue(ServiceCacheKey)"],"observation":"~1,278 samples in DI resolution. Moderate cost suggesting per-request service resolution is happening frequently. If scoped services are being resolved in tight loops, consider injecting once and reusing."}],"summary":"CPU time is overwhelmingly dominated by SQL data reading (~10% exclusive) and its downstream effects: Unicode string decoding (1.2%), type casting from materialization (4.1%), EF Core change tracking (1.8%), and JSON serialization of large payloads (2.3%). The profile strongly indicates the API is fetching too many rows and/or too many columns from the database — a classic unbounded query or N+1 pattern. The three highest-impact optimizations are: (1) add pagination or stricter WHERE clauses to limit result set size, (2) use .AsNoTracking() for read-only queries to eliminate change tracking overhead, and (3) use projection (Select) to return only needed columns instead of full entities. The SortedDictionary usage (2.3%) is also suspicious — if sort order isn't required, switching to Dictionary/HashSet would save significant enumeration cost."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.82,"gen1Rate":0.78,"gen2Rate":0.05,"pauseTimeMs":{"avg":197.3,"max":1772.5,"total":38862.6},"gcPauseRatio":32.7,"fragmentationPct":0.0,"observations":["GC pause ratio of 32.7% is catastrophic — the application spends nearly one-third of its time paused for garbage collection, directly throttling throughput and inflating latency","Gen1 collection count (93) is nearly equal to Gen0 (98), meaning almost every Gen0 collection promotes surviving objects into Gen1 — this indicates mid-lived objects that outlive Gen0 but die in Gen1, possibly request-scoped allocations held across async awaits or buffered response data","Average Gen0 pause of 203.9ms is ~20x higher than healthy (should be <10ms), indicating the managed heap is extremely large at collection time and the GC must scan/compact a massive nursery","Max pause of 1772.5ms (Gen1) directly explains the 482ms p95 latency and likely causes the 11.11% error rate via request timeouts during stop-the-world pauses","Gen2 collections are low (6) with a comparatively lower avg pause (116.4ms), so long-lived object pressure is not the primary issue — the problem is in the Gen0→Gen1 promotion pipeline","Total allocation volume of 62.3GB over ~119 seconds yields an allocation rate of ~524 MB/sec, which is extreme and the root driver of all GC pressure"]},"heapAnalysis":{"peakSizeMB":2180.57,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 2.18GB is extremely large for an API service — this suggests either massive in-memory buffering, unbounded caches, or large result sets being materialized into memory","Total allocations of 62.3GB during the test (~524 MB/sec) means the application allocates and discards enormous volumes of objects, forcing continuous GC activity","Fragmentation is 0%, so LOH fragmentation and pinning are not contributing factors — the issue is pure allocation volume and object lifetime"]},"topAllocators":[{"type":"(no type-level data available)","allocMB":null,"pctOfTotal":null,"callSite":"unknown — allocation tick data was not captured or exported","observation":"Re-run diagnostics with /DotNetAllocSampled and export GC Heap Alloc Stacks to identify the specific types and call sites responsible for the 524 MB/sec allocation rate. Without this data, focus on common patterns: large EF Core result sets materialized via ToListAsync without pagination, string-heavy serialization, or unbounded response buffering."}],"summary":"The application is in severe GC distress: a 32.7% GC pause ratio means one-third of execution time is lost to garbage collection, driven by an allocation rate of ~524 MB/sec and a peak heap of 2.18GB. The near-equal Gen0/Gen1 collection counts reveal that most allocated objects survive just long enough to be promoted to Gen1 before dying — a classic pattern of request-scoped buffers, large EF Core materializations, or un-pooled objects held across async boundaries. The #1 fix is to reduce allocation volume: paginate or stream large database query results instead of materializing full result sets into memory, pool frequently allocated objects (ArrayPool<T>, ObjectPool<T>), and use streaming serialization to avoid buffering entire responses. This alone should dramatically reduce GC frequency, cut pause times, and resolve both the high p95 latency and the error rate caused by GC-induced timeouts."}
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
