Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 7)
- p95 Latency: 685.029124999999ms
- Requests/sec: 884.3
- Error rate: 0%
- Improvement vs baseline: 90.9%

## Baseline Performance
- p95 Latency: 7546.103045ms
- Requests/sec: 125.5
- Error rate: 0%

## Runtime Counters
- CPU avg: 24.11%
- GC heap max: 2883MB
- Gen2 collections: 0
- Thread pool max threads: 57

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


## Known Optimization Queue
- [TRIED] `SampleApi/Pages/Orders/Index.cshtml.cs` — Eliminate full-table scans and N+1 queries in Orders page *(experiment 4 — improved)*
- [TRIED] `SampleApi/Controllers/CartController.cs` — Replace full CartItems table scans with server-side filtering *(experiment 5 — improved)*
- [TRIED] `SampleApi/Pages/Products/Detail.cshtml.cs` — Eliminate full Reviews and Products table scans in product detail page *(experiment 6 — improved)*

## Last Experiment's Fix
Eliminate full Reviews and Products table scans in product detail page

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


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":1.2,"exclusivePct":1.2,"callChain":["EF Core Query Pipeline","SqlDataReader.ReadAsync","TdsParser.TryRun","TdsParserStateObject.TryReadChar"],"observation":"Highest exclusive-time managed method (8875 samples). Character-by-character TDS stream parsing dominates the SQL read path — suggests the query is returning large volumes of string/nvarchar data, possibly wide SELECT * results or large text columns."},{"method":"SQL Server Engine (sqlmin + sqllang + sqltses + sqldk)","inclusivePct":13.5,"exclusivePct":13.5,"callChain":["Application Query","SqlCommand.ExecuteReaderAsync","TdsParser.TryRun","Named Pipe/Shared Memory","SQL Server Engine"],"observation":"Combined ~100K samples in SQL Server engine modules. The database is the single largest CPU consumer, indicating heavy query workload — likely missing indexes, unfiltered queries, or N+1 patterns causing excessive round-trips."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.TryReadColumnInternal","inclusivePct":0.5,"exclusivePct":0.5,"callChain":["EF Core SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TryReadInternal","TryReadColumnInternal"],"observation":"3612 samples reading column data. Combined with TryGetTokenLength (2537), TryProcessColumnHeaderNoNBC (1738), and TryReadSqlValue (1428), the column-reading pipeline totals ~10K+ samples — indicates queries returning many columns or many rows with wide schemas."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1+AsyncEnumerator.MoveNextAsync","inclusivePct":0.27,"exclusivePct":0.27,"callChain":["Controller Action","ToListAsync","SingleQueryingEnumerable.MoveNextAsync"],"observation":"1989 samples in EF Core's row enumeration. Combined with ToListAsync (755), this is the main query materialization path. High sample counts here plus massive SQL Server time suggest queries are returning too many rows — add server-side filtering, pagination, or projection."},{"method":"Microsoft.EntityFrameworkCore.ChangeTracking.Internal.NavigationFixer.InitialFixup + InternalEntityEntry.ctor","inclusivePct":0.19,"exclusivePct":0.19,"callChain":["ToListAsync","MoveNextAsync","StateManager.StartTrackingFromQuery","NavigationFixer.InitialFixup"],"observation":"NavigationFixer (756) + InternalEntityEntry ctor (628) = 1384 samples in EF change tracking. Materializing and tracking large numbers of entities is expensive. Use AsNoTracking() for read-only queries to eliminate this overhead entirely."},{"method":"System.Collections.Generic.SortedDictionary`2 Enumeration","inclusivePct":0.72,"exclusivePct":0.72,"callChain":["EF Core ChangeTracking","StateManager/IdentityMap","SortedDictionary.ValueCollection.Enumerator.MoveNext"],"observation":"SortedDictionary enumeration totals ~5300 samples (MoveNext 1304+1275+957, GetCurrent 748+542, GetEnumerator 572+544). This is EF Core's internal identity map being walked during entity fixup — further evidence that change tracking over large result sets is costly."},{"method":"System.Text.UnicodeEncoding.GetCharCount + GetChars","inclusivePct":0.88,"exclusivePct":0.88,"callChain":["SqlDataReader.GetString","TdsParser.TryReadSqlStringValue","TryReadPlpUnicodeCharsChunk","UnicodeEncoding.GetCharCount/GetChars"],"observation":"Unicode decoding (GetCharCount 4228 + GetChars 2317 + CreateStringFromEncoding 739 + TryReadPlpUnicodeCharsChunk 1622) totals ~8900 samples. Heavy nvarchar/ntext column processing — consider returning only needed columns via projection instead of full entities."},{"method":"System.Text.Json Serialization Pipeline","inclusivePct":0.59,"exclusivePct":0.59,"callChain":["Kestrel Response Pipeline","JsonSerializer.SerializeAsync","ObjectDefaultConverter.OnTryWrite","StringConverter.Write"],"observation":"JSON serialization totals ~4370 samples (StringConverter.Write 2343, ObjectDefaultConverter.OnTryWrite 669, JsonWriterHelper.ToUtf8 657, TextEncoder 701). Serializing large object graphs with many string properties — reduce payload size via projection or DTO mapping with fewer fields."},{"method":"System.Runtime.CompilerServices.CastHelpers (ChkCast + IsInstance)","inclusivePct":1.54,"exclusivePct":1.54,"callChain":["EF Core Materialization","QueryingEnumerable.MoveNextAsync","CastHelpers.ChkCastAny/IsInstanceOfClass/IsInstanceOfInterface"],"observation":"Type checking totals ~11.5K samples (ChkCastAny 2910, IsInstanceOfClass 2827, IsInstanceOfInterface 2525, ChkCastInterface 2516, IsInstanceOfAny 703). High casting overhead is characteristic of EF Core materializing polymorphic entities or navigating complex inheritance hierarchies."},{"method":"System.Threading.ExecutionContext.Capture + SetLocalValue","inclusivePct":0.41,"exclusivePct":0.41,"callChain":["AsyncMethodBuilderCore.Start","ExecutionContext.Capture","ExecutionContext.SetLocalValue"],"observation":"Async context overhead totals ~3040 samples (Capture 1112, SetLocalValue 1059, OnValuesChanged 868). Each async/await captures ExecutionContext — deeply nested async call chains in the EF/SQL pipeline amplify this. Reducing query count would reduce async transitions proportionally."}],"summary":"The CPU profile is dominated by SQL Server engine processing (~13.5% of samples) and the .NET SQL client data-reading pipeline (~5% across TDS parsing, column reading, and Unicode decoding), indicating the application is moving large volumes of data from the database. EF Core change tracking and entity fixup (NavigationFixer, SortedDictionary enumeration, type casting) add significant overhead materializing these results. The most actionable optimizations are: (1) add server-side filtering/pagination to reduce row counts, (2) use Select() projections to fetch only needed columns instead of full entities, (3) apply AsNoTracking() for read-only queries to eliminate change-tracking overhead, and (4) investigate whether N+1 query patterns are causing excessive database round-trips — the sheer volume of SQL Server CPU time at 685ms p95 latency strongly suggests this."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.08,"gen1Rate":0.24,"gen2Rate":0.28,"pauseTimeMs":{"avg":91.0,"max":247.0,"total":6553.1},"gcPauseRatio":5.5,"fragmentationPct":0.0,"observations":["Inverted GC generation distribution (Gen2=33 > Gen1=29 > Gen0=10) is highly abnormal — this indicates massive Large Object Heap (LOH) allocations bypassing Gen0/Gen1 and landing directly in Gen2","Gen0 and Gen1 average pause times (111.7ms and 122.4ms) are extremely high — typical healthy values are under 10ms, suggesting these collections are being forced to compact a bloated heap","Gen2 collections at 33 over ~120s (0.28/sec) with 57ms average pause indicate the runtime is under constant pressure to reclaim long-lived or LOH memory","Max GC pause of 247ms is catastrophic for latency — a single Gen0 pause alone accounts for 36% of the p95 latency target","GC pause ratio of 5.5% exceeds the 5% concern threshold — the application is spending 6.5 seconds out of every 2 minutes frozen in GC"]},"heapAnalysis":{"peakSizeMB":3422.87,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 3.4GB is extremely large for an API service — this strongly suggests large object allocations (byte arrays, strings >85KB, large collections) are inflating the managed heap","Total allocation volume of 95.4GB over ~120s implies an allocation rate of ~800 MB/sec, which is extraordinarily high and the primary driver of GC pressure","Zero fragmentation suggests the GC is compacting effectively, but at the cost of long pause times — the heap is simply too large to compact cheaply"]},"topAllocators":[{"type":"Unknown (allocation type data unavailable)","allocMB":null,"pctOfTotal":null,"callSite":"Unknown — PerfView allocation tick data was not captured or export failed","observation":"With 95.4GB total allocations and a 3.4GB peak heap, the likely culprits are: (1) large byte[] buffers for serialization/deserialization, (2) unbounded LINQ materializations (.ToList() on large query results), (3) large string allocations from JSON serialization, or (4) EF Core change tracking holding excessive object graphs. Re-run PerfView with /DotNetAllocSampled and export GC Heap Alloc Stacks to identify exact types."}],"summary":"The API is allocating ~800 MB/sec with a 3.4GB peak heap, causing an inverted GC pattern where Gen2 collections (33) outnumber Gen0 (10) — a clear sign of massive LOH allocations (objects >85KB). GC pauses average 91ms with a 247ms max, directly contributing to the 685ms p95 latency. The #1 fix is to eliminate large object allocations: look for unbounded .ToList() on EF queries, large byte[]/string buffers in serialization paths, and missing pagination — switch to streaming (IAsyncEnumerable), pooled buffers (ArrayPool<T>), and server-side pagination to keep allocations small and short-lived."}
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
