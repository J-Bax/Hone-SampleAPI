Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 11)
- p95 Latency: 496.6681ms
- Requests/sec: 1295.4
- Error rate: 11.11%
- Improvement vs baseline: 75.8%

## Baseline Performance
- p95 Latency: 2054.749925ms
- Requests/sec: 427.3
- Error rate: 11.11%

## Runtime Counters
- CPU avg: 21.68%
- GC heap max: 2206MB
- Gen2 collections: 30671096
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


## Known Optimization Queue
- [TRIED] `SampleApi/Program.cs` — Enable DbContext pooling to reduce per-request allocation and GC pressure *(experiment 10 — regressed)*
- [PENDING] [ARCHITECTURE] `SampleApi/Data/AppDbContext.cs` — Add database indexes for all high-traffic query filter columns

## Last Experiment's Fix
Replace tracked FindAsync existence checks with AnyAsync and add AsNoTracking

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


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":1.41,"exclusivePct":1.41,"callChain":["ToListAsync","SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun","TdsParser.TryReadSqlValue","TdsParserStateObject.TryReadChar"],"observation":"Highest exclusive application-level CPU consumer. Character-by-character reading of SQL nvarchar data indicates the API is transferring large volumes of string-heavy rows from the database — likely fetching full entities with wide text columns instead of projecting only needed fields."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1.MoveNextAsync","inclusivePct":9.1,"exclusivePct":0.22,"callChain":["EntityFrameworkQueryableExtensions.ToListAsync","SingleQueryingEnumerable.MoveNextAsync"],"observation":"Low exclusive but very high inclusive CPU — this is the query execution entry point driving the entire SqlClient TDS parsing pipeline (~33K samples), entity materialization, change tracking, and navigation fixup below it. The query is materializing many rows into fully-tracked entities; consider pagination, Select() projection, and AsNoTracking()."},{"method":"Microsoft.EntityFrameworkCore.ChangeTracking.Internal.StateManager.StartTrackingFromQuery","inclusivePct":2.3,"exclusivePct":0.07,"callChain":["SingleQueryingEnumerable.MoveNextAsync","StateManager.StartTrackingFromQuery","InternalEntityEntry..ctor","NavigationFixer.InitialFixup"],"observation":"Change tracking overhead for what appears to be read-only query results. InternalEntityEntry construction (679 samples), NavigationFixer.InitialFixup (536 samples), and SortedDictionary enumeration within fixup (~8,500 samples) all indicate unnecessary tracking. Adding AsNoTracking() would eliminate this entire cost and the associated SortedDictionary/SortedSet overhead."},{"method":"System.Collections.Generic.SortedSet`1.Enumerator.MoveNext / SortedDictionary enumeration","inclusivePct":1.11,"exclusivePct":1.11,"callChain":["StateManager.StartTrackingFromQuery","NavigationFixer.InitialFixup","SortedDictionary.ValueCollection.GetEnumerator","SortedSet.Enumerator.MoveNext"],"observation":"~8,500 exclusive samples across SortedDictionary/SortedSet methods (MoveNext, GetEnumerator, get_Current, constructor). These O(log n) tree-traversal collections are used by EF Core's NavigationFixer to wire up relationships between tracked entities. The high sample count signals many related entities being loaded and fixed up — a hallmark of eager-loading large object graphs without AsNoTracking()."},{"method":"System.Text.Json.Serialization.Converters.StringConverter.Write","inclusivePct":1.03,"exclusivePct":0.54,"callChain":["JsonSerializer.SerializeAsync","ObjectDefaultConverter.OnTryWrite","JsonPropertyInfo.GetMemberAndWriteJson","StringConverter.Write"],"observation":"JSON serialization consumes ~7,900 samples across StringConverter.Write (4,132), ObjectDefaultConverter.OnTryWrite (1,103), text encoding (1,082), UTF-8 conversion (1,052), and property writing (535). This indicates large response payloads with many string properties — reducing result set size through pagination and projection would proportionally reduce serialization cost."},{"method":"System.Runtime.CompilerServices.CastHelpers.ChkCastAny","inclusivePct":2.08,"exclusivePct":0.55,"callChain":["SingleQueryingEnumerable.MoveNextAsync","EntityMaterializer","CastHelpers.ChkCastAny"],"observation":"Type-casting methods (ChkCastAny 4,255 + IsInstanceOfInterface 3,512 + IsInstanceOfClass 3,042 + ChkCastInterface 2,714 = ~13,500 samples) reflect framework overhead from entity materialization, DI resolution, and collection operations. Not directly optimizable, but reducing entity counts and using AsNoTracking() will cascade into lower casting overhead."},{"method":"System.Text.UnicodeEncoding.GetCharCount","inclusivePct":0.84,"exclusivePct":0.35,"callChain":["TdsParser.TryReadSqlStringValue","String.CreateStringFromEncoding","UnicodeEncoding.GetCharCount"],"observation":"Unicode decoding of SQL nvarchar data (~6,500 samples across GetCharCount, GetChars, CreateStringFromEncoding). Combined with TryReadPlpUnicodeCharsChunk (1,959) and TryReadSqlStringValue (631), this confirms the API reads large amounts of string data from the database — likely full text columns that could be excluded via projection."},{"method":"Microsoft.Extensions.DependencyInjection.ResolveService","inclusivePct":0.22,"exclusivePct":0.12,"callChain":["Kestrel.RequestProcessing","ResolveService","Dictionary.FindValue(ServiceCacheKey)"],"observation":"DI service resolution (892 samples) with Dictionary lookups on ServiceCacheKey (812 samples). Moderate overhead suggesting per-request resolution of multiple services. Lower priority than the database access issues, but consider registering hot-path services as singletons where safe."}],"summary":"The CPU profile is overwhelmingly dominated by database data access: SQL Server engine processing (sqlmin+sqllang ~10% of total CPU), SqlClient TDS wire-protocol parsing (~4.3%), EF Core entity materialization and change tracking (~2.3%), and downstream effects (type casting ~2%, SortedDictionary navigation fixup ~1.1%, Unicode string decoding ~0.8%). The application loads large, fully-tracked entity graphs with many string columns and serializes them into sizable JSON responses. The three highest-impact optimizations are: (1) add AsNoTracking() to read-only queries to eliminate change tracking, navigation fixup, and SortedDictionary overhead entirely; (2) use Select() projections to fetch only required columns, drastically reducing TDS parsing, Unicode decoding, and JSON serialization costs; and (3) implement server-side pagination to cap result set size per request — together these should reduce the dominant SQL-to-JSON pipeline CPU by 50%+ and address the 496ms p95 latency and 11% error rate (likely timeouts under load)."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":1.08,"gen1Rate":1.01,"gen2Rate":0.05,"pauseTimeMs":{"avg":8.87,"max":89.4,"total":2235.4},"gcPauseRatio":1.9,"fragmentationPct":0.0,"observations":["Gen1:Gen0 ratio is 0.94 (119/127), extremely abnormal — nearly every Gen0 collection promotes survivors to Gen1, indicating mid-lived objects that outlive Gen0 but die before Gen2. This points to request-scoped allocations (e.g., large EF Core result sets, serialized DTOs, or buffered response bodies) that survive the first GC but are abandoned shortly after.","Gen1 max pause of 89.4ms directly contributes to the 496ms p95 latency — any request that coincides with a Gen1 collection takes an ~89ms hit, which combined with normal processing time pushes latency past thresholds.","Gen2 collections are healthy (6 total, 2.5ms avg pause) — long-lived objects and LOH are well-managed, and fragmentation is 0%.","Total allocation volume is ~93.7 GB over ~118s (~794 MB/sec throughput allocation rate). This is extremely high for an API doing 1295 req/sec — roughly 612 KB allocated per request, suggesting large object graphs or unbounded query results per request.","GC pause ratio of 1.9% is below the 5% concern threshold, but the max pause spikes (89.4ms) are the real problem — they create tail latency outliers rather than steady-state throughput loss."]},"heapAnalysis":{"peakSizeMB":2116.05,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 2.1 GB is very large for an API service handling 1295 req/sec — this suggests either unbounded query results being materialized in memory, lack of pagination/streaming, or large intermediate buffers.","With 93.7 GB total allocated and 2.1 GB peak, the churn ratio is ~44x — objects are allocated and collected rapidly, confirming heavy short-to-mid-lived allocation pressure.","Zero fragmentation is positive — LOH compaction is not an issue, and pinned objects are not causing memory holes."]},"topAllocators":[{"type":"(allocation type data unavailable)","allocMB":null,"pctOfTotal":null,"callSite":"Unknown — PerfView allocation tick data was not captured or export yielded no type breakdown","observation":"Re-run diagnostics with /DotNetAllocSampled and export via 'GC Heap Alloc Stacks' view to identify the specific types driving 794 MB/sec allocation rate. Given the 612 KB/request footprint, likely culprits are: (1) large List<T> or array materializations from EF Core queries without pagination, (2) string serialization buffers from JSON response writing, (3) byte[] buffers from response body buffering."}],"summary":"The dominant issue is a near-1:1 Gen1-to-Gen0 collection ratio, meaning almost all allocated objects survive Gen0 and must be promoted and later collected in Gen1 — this drives 89ms max GC pauses that spike p95 latency. With ~794 MB/sec allocation throughput (~612 KB per request), the most likely root cause is unbounded EF Core query materialization (e.g., loading entire tables without pagination or streaming). The #1 fix should be adding server-side pagination or IAsyncEnumerable streaming to the heaviest endpoints to reduce per-request allocation volume and eliminate the mid-lived object promotion pressure that triggers expensive Gen1 collections."}
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
