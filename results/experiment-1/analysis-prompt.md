Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 1)
- p95 Latency: 1596.242785ms
- Requests/sec: 468.5
- Error rate: 11.11%
- Improvement vs baseline: 0%

## Baseline Performance
- p95 Latency: 1596.242785ms
- Requests/sec: 468.5
- Error rate: 11.11%


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


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.SqlDataReader.TryReadColumnInternal","inclusivePct":15.2,"exclusivePct":1.0,"callChain":["ToListAsync","SingleQueryingEnumerable.MoveNextAsync","TdsParser.TryRun","SqlDataReader.TryReadColumnInternal"],"observation":"Top-level SQL data reading method with massive inclusive cost. The sheer volume of column reads indicates the application is fetching far too many rows or columns per query — likely selecting entire tables or missing pagination/projection."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1+AsyncEnumerator.MoveNextAsync","inclusivePct":12.8,"exclusivePct":0.6,"callChain":["ToListAsync.MoveNext","SingleQueryingEnumerable.MoveNextAsync","TdsParser.TryRun","SqlDataReader.*"],"observation":"EF Core query enumeration driving bulk of SQL reads. Each MoveNext materializes a row through the full TDS parsing pipeline. High sample count suggests queries return very large result sets that are fully enumerated into memory."},{"method":"Microsoft.EntityFrameworkCore.ChangeTracking.Internal.NavigationFixer.InitialFixup","inclusivePct":4.8,"exclusivePct":0.24,"callChain":["StartTrackingFromQuery","StateManager.StartTracking","NavigationFixer.InitialFixup","SortedDictionary.ValueCollection.Enumerator.MoveNext"],"observation":"Navigation property fixup is iterating SortedDictionary collections for every tracked entity. This O(n²) behavior worsens as more entities are tracked — strong signal that queries are loading large graphs of related entities with change tracking enabled."},{"method":"System.Collections.Generic.SortedDictionary`2+ValueCollection+Enumerator.MoveNext","inclusivePct":3.4,"exclusivePct":3.4,"callChain":["NavigationFixer.InitialFixup","SortedDictionary.ValueCollection.GetEnumerator","SortedSet.Enumerator.MoveNext"],"observation":"SortedDictionary/SortedSet enumeration consuming 3.4% exclusive CPU across ~33K samples. This is EF Core's internal identity map iteration during change tracking — the cost scales with the number of tracked entities. Use AsNoTracking() for read-only queries."},{"method":"Microsoft.EntityFrameworkCore.ChangeTracking.Internal.StateManager.StartTrackingFromQuery","inclusivePct":3.2,"exclusivePct":0.2,"callChain":["SingleQueryingEnumerable.MoveNextAsync","StateManager.StartTrackingFromQuery","InternalEntityEntry.ctor","IdentityMap.Add"],"observation":"Every materialized entity goes through full change tracking setup: identity map lookup, snapshot creation, shadow property initialization, and navigation fixup. For read-only API endpoints this is entirely wasted CPU."},{"method":"Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync","inclusivePct":18.5,"exclusivePct":0.22,"callChain":["Controller.Action","ToListAsync","SingleQueryingEnumerable.MoveNextAsync"],"observation":"ToListAsync is the top application-level call site — it forces full materialization of query results into a List. If queries lack proper .Where() filters, .Select() projections, or .Take() limits, this materializes entire tables into memory."},{"method":"Microsoft.Data.SqlClient.TdsParser.TryRun","inclusivePct":8.5,"exclusivePct":0.4,"callChain":["SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.TryReadInternal","TdsParser.TryRun","TdsParser.TryReadSqlValue"],"observation":"TDS protocol parsing is the main CPU consumer within SQL Client. The high inclusive cost reflects parsing thousands of rows with multiple string/int columns. Reducing result set size would proportionally reduce this cost."},{"method":"System.Text.UnicodeEncoding.GetCharCount + GetChars","inclusivePct":1.5,"exclusivePct":1.5,"callChain":["TdsParser.TryReadSqlStringValue","TdsParserStateObject.TryReadString","UnicodeEncoding.GetCharCount/GetChars","String.CreateStringFromEncoding"],"observation":"Unicode string decoding accounts for ~15K exclusive samples — driven by reading many string columns from SQL Server. This is proportional to the volume of varchar/nvarchar data being transferred. Projection (SELECT only needed columns) would reduce this."},{"method":"Microsoft.Extensions.Logging.MessageLogger.IsEnabled + EF Diagnostics","inclusivePct":0.7,"exclusivePct":0.7,"callChain":["EFCore.Diagnostics.ShouldLog","MessageLogger.IsEnabled","DiagnosticSourceEventSource.FilterAndTransform"],"observation":"Logging checks (~7.2K samples across IsEnabled, ShouldLog, NeedsEventData, LogStartedTracking) are called per-entity and per-query. While each call is cheap, the volume makes it measurable. Consider raising minimum log level in production load tests."},{"method":"System.Runtime.CompilerServices.CastHelpers (ChkCast/IsInstance)","inclusivePct":2.5,"exclusivePct":2.5,"callChain":["EFCore materialization pipeline","CastHelpers.ChkCastInterface/ChkCastAny/IsInstanceOfClass"],"observation":"Type-checking operations total ~23K exclusive samples — a side effect of EF Core's heavy use of polymorphism during entity materialization. This overhead is proportional to entity count and is an indirect indicator of over-fetching."}],"summary":"The CPU profile is dominated by SQL data reading and EF Core entity materialization with change tracking, which together account for the vast majority of application CPU time. The clearest optimization targets are: (1) Add .AsNoTracking() to read-only queries to eliminate the expensive NavigationFixer/StateManager/IdentityMap overhead that scales quadratically with entity count; (2) Add .Select() projections to return only needed columns instead of full entities, reducing TDS parsing, string decoding, and materialization cost; (3) Add .Where() filters and/or pagination (.Take()/.Skip()) to limit result set sizes — the current profile suggests queries may be returning hundreds or thousands of rows per request, explaining the 1596ms p95 latency and 11% error rate (likely timeouts). The SortedDictionary enumeration pattern in NavigationFixer is a particularly strong signal that large entity graphs are being tracked unnecessarily."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.07,"gen1Rate":0.6,"gen2Rate":1.92,"pauseTimeMs":{"avg":55.4,"max":196.0,"total":17346.8},"gcPauseRatio":14.3,"fragmentationPct":0.0,"observations":["CRITICAL: GC generation distribution is inverted — 232 Gen2 collections vs only 9 Gen0 and 72 Gen1. Normal apps show Gen0 >> Gen1 >> Gen2. This pattern strongly indicates massive Large Object Heap (LOH) allocations (objects >85KB bypass Gen0/Gen1 and land directly in Gen2/LOH), or extreme allocation pressure causing objects to be promoted before Gen0 can collect them.","GC pause ratio of 14.3% is nearly 3x the 5% concern threshold — the application spends 1 out of every 7 seconds paused for garbage collection, directly throttling throughput and inflating latency.","Max GC pause of 196ms (Gen0) directly contributes to the 1596ms p95 latency. Multiple back-to-back GC pauses during a single request lifecycle can stack to hundreds of milliseconds.","Gen0 avg pause of 128ms is abnormally high (typical Gen0 pauses are <10ms). This suggests the Gen0 heap is enormous at collection time, likely because the allocator is outrunning the collector.","Gen1 collections (72) are 8x Gen0 collections (9), which is physically impossible under normal GC behavior — Gen1 collects only when Gen0 triggers it. This confirms the runtime is under extreme memory pressure, likely forcing aggressive background Gen2 collections that subsume Gen0/Gen1 work.","Total allocations of ~236GB over ~121s yields an allocation rate of ~1,950 MB/sec — this is extreme and the root cause of all GC pressure. The application is allocating and discarding data at a rate that overwhelms the garbage collector."]},"heapAnalysis":{"peakSizeMB":1976.06,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak managed heap of ~2GB under load is very high for an API service. Combined with 236GB total allocations, the heap is churning through roughly 120x its own size during the test — objects are allocated and discarded at an unsustainable rate.","Zero fragmentation suggests the GC is compacting effectively, but the sheer volume of allocations means compaction work is constant and expensive.","The 2GB peak with ~1,950 MB/sec allocation rate means the entire heap contents turn over approximately once per second — virtually nothing is being reused or cached."]},"topAllocators":[{"type":"(allocation type data unavailable)","allocMB":null,"pctOfTotal":null,"callSite":"Unknown — PerfView allocation tick sampling returned no type breakdown","observation":"Allocation type data is missing. However, given the 236GB total allocation volume and the inverted GC generation pattern, the most likely culprits are: (1) large byte arrays or MemoryStream buffers from serialization/deserialization (JSON response bodies, database result materialization), (2) string allocations from query building or response formatting, (3) Entity Framework change tracker allocations from untracked queries not using AsNoTracking(), (4) large LINQ materializations (ToList() on large result sets creating List<T> with backing arrays >85KB that go to LOH)."}],"summary":"The application is critically GC-bound: 14.3% of execution time is spent in garbage collection, driven by an extreme allocation rate of ~1,950 MB/sec (236GB total). The inverted generation pattern (232 Gen2 vs 9 Gen0 collections) points to massive Large Object Heap allocations — likely large arrays or buffers from Entity Framework result materialization, JSON serialization, or unbounded query results. The #1 fix is to identify and eliminate the source of large (>85KB) allocations: add pagination to database queries to limit result set sizes, use AsNoTracking() for read-only EF queries, enable response streaming instead of buffering, and pool or reuse byte buffers via ArrayPool<T>. Reducing allocation volume by even 50% should dramatically cut GC pauses and improve p95 latency from the current 1596ms."}
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
