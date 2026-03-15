Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 7)
- p95 Latency: 583.09564ms
- Requests/sec: 1075.8
- Error rate: 11.11%
- Improvement vs baseline: 63.5%

## Baseline Performance
- p95 Latency: 1596.242785ms
- Requests/sec: 468.5
- Error rate: 11.11%

## Runtime Counters
- CPU avg: 37.68%
- GC heap max: 2085MB
- Gen2 collections: 1
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
| 4 | 2026-03-15 05:40 | `SampleApi/Pages/Checkout/Index.cshtml.cs` | Per-item SaveChanges and full table scans in Checkout | improved |
| 5 | 2026-03-15 06:05 | `SampleApi/Pages/Products/Detail.cshtml.cs` | Full table scans of Reviews and Products in product detail page | regressed |
| 6 | 2026-03-15 06:30 | `SampleApi/Pages/Orders/Index.cshtml.cs` | Triple full table scan with N+1 on growing tables in order history | improved |


## Known Optimization Queue
- [TRIED] `SampleApi/Pages/Checkout/Index.cshtml.cs` — Per-item SaveChanges and full table scans in Checkout *(experiment 4 — improved)*
- [TRIED] `SampleApi/Pages/Products/Detail.cshtml.cs` — Full table scans of Reviews and Products in product detail page *(experiment 5 — regressed)*
- [TRIED] `SampleApi/Pages/Orders/Index.cshtml.cs` — Triple full table scan with N+1 on growing tables in order history *(experiment 6 — improved)*

## Last Experiment's Fix
Triple full table scan with N+1 on growing tables in order history

## Experiment History (with metrics)
Do NOT re-attempt optimizations that were already tried and resulted in stale or regressed outcomes. Propose different targets or approaches instead.
| Exp | File | Outcome | p95 (ms) | RPS | Branch |
|-----|------|---------|----------|-----|--------|
| 1 | — | improved | 1557.3 | 474.2 | hone/experiment-1 |
| 2 | — | improved | 1581.7 | 476.3 | hone/experiment-2 |
| 3 | — | improved | 1526.7 | 494.4 | hone/experiment-3 |
| 4 | — | improved | 1555 | 484.7 | hone/experiment-4 |
| 5 | — | regressed | 1748.9 | 491.7 | hone/experiment-5 |
| 6 | — | improved | 583.1 | 1075.8 | hone/experiment-6 |


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":8.5,"exclusivePct":1.45,"callChain":["ToListAsync","SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun","TdsParserStateObject.TryReadChar"],"observation":"Highest exclusive-sample application method (12,905 samples). Reads individual characters from the TDS stream — volume indicates the API is fetching large string-heavy result sets from SQL Server, likely returning far more rows or columns than needed."},{"method":"System.Collections.Generic.SortedDictionary/SortedSet Enumeration (aggregate)","inclusivePct":1.8,"exclusivePct":1.57,"callChain":["Controller Action","SortedDictionary.Values.GetEnumerator","SortedSet.Enumerator.MoveNext","SortedSet.Enumerator.Initialize"],"observation":"~13,900 combined samples across SortedDictionary/SortedSet MoveNext, GetEnumerator, get_Current, and Stack allocation. SortedDictionary has O(log n) enumeration with high allocation overhead — this strongly suggests application code is using SortedDictionary where Dictionary or a pre-sorted list would be far more efficient."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.TryReadColumnInternal","inclusivePct":5.2,"exclusivePct":0.7,"callChain":["SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TryReadInternal","TryReadColumnInternal"],"observation":"6,253 exclusive samples in column-level deserialization. Combined with TryProcessColumnHeaderNoNBC (2,740) and WillHaveEnoughData (2,116), indicates the query returns many columns per row. SELECT * pattern or over-fetching entity properties is likely."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable.MoveNextAsync","inclusivePct":42.0,"exclusivePct":0.36,"callChain":["ToListAsync","MoveNextAsync","→ SqlDataReader","→ TdsParser","→ StateManager.StartTracking"],"observation":"Low exclusive (3,240) but extremely high inclusive — this is the main EF Core query materialization loop driving nearly all SQL reading, change tracking, and object allocation. The ratio confirms the bottleneck is data volume flowing through this path, not the method itself."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.PrepareAsyncInvocation","inclusivePct":0.65,"exclusivePct":0.43,"callChain":["SqlDataReader.ReadAsync","PrepareAsyncInvocation"],"observation":"3,860 samples in async preparation overhead, called once per row read. Combined with StateSnapshot.Snap (2,611) and StateSnapshot.Clear (1,389), the per-row async bookkeeping is ~8,860 samples total — a tax proportional to row count that confirms over-fetching."},{"method":"System.Text.UnicodeEncoding.GetCharCount + GetChars","inclusivePct":1.4,"exclusivePct":1.39,"callChain":["TdsParser.TryReadPlpUnicodeCharsChunk","UnicodeEncoding.GetCharCount","UnicodeEncoding.GetChars","String.CreateStringFromEncoding"],"observation":"12,333 combined samples in Unicode string decoding from SQL. Together with TryReadPlpUnicodeCharsChunk (2,450) and TryReadSqlStringValue (1,120), indicates the API reads many large nvarchar columns — consider projecting only needed string fields."},{"method":"Microsoft.EntityFrameworkCore.ChangeTracking.Internal (aggregate)","inclusivePct":1.5,"exclusivePct":0.33,"callChain":["SingleQueryingEnumerable.MoveNextAsync","StateManager.StartTrackingFromQuery","NavigationFixer.InitialFixup","InternalEntityEntry..ctor"],"observation":"~2,890 samples across NavigationFixer.InitialFixup (1,087), StateManager.StartTrackingFromQuery (1,030), and InternalEntityEntry..ctor (773). For read-only API endpoints, AsNoTracking() would eliminate this entire cost and reduce Dictionary/casting overhead downstream."},{"method":"System.Text.Json Serialization (aggregate)","inclusivePct":1.2,"exclusivePct":0.58,"callChain":["Controller Action","JsonSerializer.SerializeAsync","ObjectDefaultConverter.OnTryWrite","StringConverter.Write"],"observation":"~5,079 samples across StringConverter.Write (2,649), JsonWriterHelper.ToUtf8 (857), ObjectDefaultConverter.OnTryWrite (808), and TextEncoder (765). JSON serialization cost is proportional to response payload size — reducing the number of entities/fields returned would lower this."},{"method":"System.Runtime.CompilerServices.CastHelpers (aggregate)","inclusivePct":2.0,"exclusivePct":2.03,"callChain":["EF Core Materialization","CastHelpers.ChkCastAny/ChkCastInterface/IsInstanceOfClass"],"observation":"~17,981 combined samples across five CastHelpers methods. High type-checking overhead is a symptom of EF Core's polymorphic materialization pipeline processing many entities. Reducing result set size and using AsNoTracking() would proportionally reduce this CLR tax."},{"method":"System.Threading.ExecutionContext (async overhead)","inclusivePct":0.8,"exclusivePct":0.58,"callChain":["AsyncMethodBuilderCore.Start","ExecutionContext.Capture","ExecutionContext.SetLocalValue","ExecutionContext.OnValuesChanged"],"observation":"~5,135 samples in async execution context management. This is proportional to the number of await points hit per request — each row materialized triggers multiple awaits. Not directly fixable, but shrinks automatically when fewer rows are processed."}],"summary":"The CPU profile is overwhelmingly dominated by SQL data reading and materialization: TDS parsing, Unicode string decoding, column deserialization, and EF Core change tracking collectively consume the majority of application CPU time. The two most actionable optimizations are (1) reduce query result set size — add server-side filtering, pagination, or projection (Select) to avoid fetching entire tables — and (2) add AsNoTracking() to read-only queries to eliminate change-tracking overhead. The unusual SortedDictionary enumeration hotspot (~14K samples) also warrants investigation — replacing it with Dictionary or List would remove O(log n) traversal and per-enumeration Stack allocations. These changes would directly reduce the 583ms p95 latency and likely resolve the 11% error rate caused by timeouts under load."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.04,"gen1Rate":0.41,"gen2Rate":0.66,"pauseTimeMs":{"avg":82.2,"max":333.8,"total":10935.6},"gcPauseRatio":9.2,"fragmentationPct":0.0,"observations":["GC generation distribution is severely inverted: Gen2 (79) >> Gen1 (49) >> Gen0 (5). Normal applications show Gen0 >> Gen1 >> Gen2. This indicates most objects are either allocated on the Large Object Heap (>85KB, collected with Gen2) or are surviving long enough to be promoted to Gen2 rapidly.","GC pause ratio of 9.2% is nearly double the 5% concern threshold — the application spends roughly 1 in 11 seconds frozen in GC, directly degrading throughput and latency.","Max GC pause of 333.8ms (Gen1) accounts for more than half the observed p95 latency of 583ms. GC pauses are a primary contributor to tail latency.","Gen1 collections are unexpectedly expensive (avg 121.2ms, max 333.8ms), suggesting Gen1 heaps are very large due to rapid promotion from Gen0, forcing expensive compaction.","Total allocation throughput is approximately 1,273 MB/sec (151,524MB over ~119s), which is extremely high and is the root driver of all GC pressure.","The very low Gen0 count (5) relative to Gen1/Gen2 suggests the Gen0 budget is being blown through so fast that most collections are immediately escalated to higher generations."]},"heapAnalysis":{"peakSizeMB":2508.88,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak managed heap of 2,508MB is extremely large for an API service and indicates either massive in-memory data sets (e.g., unstreamed query results), large response buffering, or caching without size limits.","Total allocations of 151,524MB over ~119s means the application is churning through roughly 60x its peak heap size — objects are being created and discarded at an unsustainable rate.","Zero fragmentation suggests the heap is being fully compacted during Gen2 collections, which is consistent with the high Gen2 pause times (avg 56.3ms)."]},"topAllocators":[{"type":"(allocation type data unavailable)","allocMB":null,"pctOfTotal":null,"callSite":null,"observation":"No per-type allocation data was captured. To identify the specific types driving 1,273 MB/sec allocation rate, re-run PerfView with /DotNetAllocSampled and export the 'GC Heap Alloc Stacks' view. Given the inverted generation profile and 2.5GB peak heap, likely culprits are: (1) large byte[] or string allocations from unstreamed database result sets, (2) serialization buffers from JSON response formatting, (3) EF Core materialization of large entity graphs."}],"summary":"The application has a critical GC problem: an inverted generation profile (Gen2 collections outnumber Gen0 16:1) combined with a 9.2% GC pause ratio and 333.8ms max pause times are the dominant cause of the 583ms p95 latency and likely contribute to the 11.11% error rate via timeouts. The root cause is an extraordinarily high allocation rate (~1,273 MB/sec) producing objects large or long-lived enough to reach Gen2. The #1 fix should target reducing allocation volume — likely by streaming database results instead of buffering entire result sets in memory, which would simultaneously reduce heap peak (2.5GB), allocation rate, and Gen2 collection frequency."}
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
