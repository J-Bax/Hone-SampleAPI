Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 10)
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


## Known Optimization Queue
- [TRIED] `SampleApi/Controllers/ProductsController.cs` — Add AsNoTracking to all read-only query endpoints *(experiment 8 — regressed)*
- [TRIED] `SampleApi/Controllers/ReviewsController.cs` — Replace tracked FindAsync existence checks with AnyAsync and add AsNoTracking *(experiment 9 — improved)*

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


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":4.55,"exclusivePct":1.43,"callChain":["ToListAsync","SingleQueryingEnumerable.MoveNextAsync","TdsParser.TryRun","SqlDataReader.TryReadInternal","TdsParserStateObject.TryReadChar"],"observation":"Highest exclusive-sample application method — the TDS protocol is parsing result data character-by-character. This volume of character reads indicates queries returning large string columns or excessive row counts, pointing to missing column projections or unbounded result sets."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1.MoveNextAsync","inclusivePct":5.2,"exclusivePct":0.21,"callChain":["Controller Action","ToListAsync","SingleQueryingEnumerable.MoveNextAsync"],"observation":"High inclusive but low exclusive % — this is the main EF Core query enumeration entry point driving all downstream SQL reading. The ratio confirms that the bottleneck is not EF query setup but the sheer volume of data being materialized from SQL Server."},{"method":"System.Text.Json.Serialization.Converters.StringConverter.Write","inclusivePct":0.96,"exclusivePct":0.53,"callChain":["Controller Action","JsonSerializer.SerializeAsync","ObjectDefaultConverter.OnTryWrite","StringConverter.Write"],"observation":"String serialization alone consumes 4,080 samples — the response payloads contain many string properties. Combined with JsonWriterHelper.ToUtf8 (1,086) and text encoding (1,149), JSON serialization totals ~1% of CPU. Use DTO projections with fewer string fields or enable source-generated serializers."},{"method":"Microsoft.EntityFrameworkCore.ChangeTracking.Internal.StateManager.StartTrackingFromQuery","inclusivePct":0.75,"exclusivePct":0.1,"callChain":["SingleQueryingEnumerable.MoveNextAsync","MaterializeEntity","StateManager.StartTrackingFromQuery","NavigationFixer.InitialFixup","EntityReferenceMap.Update"],"observation":"Change tracking is active on read-only query paths — StartTrackingFromQuery (794), NavigationFixer.InitialFixup (556), and EntityReferenceMap.Update (566) total ~1,916 samples. Adding .AsNoTracking() would eliminate this overhead entirely for read endpoints."},{"method":"System.Collections.Generic.SortedDictionary`2 (enumeration cluster)","inclusivePct":1.04,"exclusivePct":1.04,"callChain":["StateManager.StartTrackingFromQuery","EntityReferenceMap.Update","SortedDictionary.Enumerator.MoveNext"],"observation":"SortedDictionary/SortedSet enumeration consumes ~7,950 samples across 8 methods. These are EF Core's internal change-tracker data structures — O(log n) per operation. This overhead scales with tracked entity count and disappears with .AsNoTracking()."},{"method":"System.Runtime.CompilerServices.CastHelpers (cluster)","inclusivePct":2.08,"exclusivePct":2.08,"callChain":["EF Core Materialization / DI Resolution","CastHelpers.ChkCastAny|IsInstanceOfInterface|IsInstanceOfClass"],"observation":"Runtime type-casting methods total ~15,900 samples (2.08%). This is driven by EF Core entity materialization (boxing/unboxing column values) and DI service resolution. Reducing tracked entities and using compiled queries would lower this."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.PrepareAsyncInvocation","inclusivePct":0.31,"exclusivePct":0.31,"callChain":["SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","SqlDataReader.PrepareAsyncInvocation"],"observation":"2,353 samples in async invocation setup — called once per row read. With large result sets, this per-row async overhead compounds. Combined with TdsParserStateObject snapshot management (Snap 1,477 + SetSnapshot 775 + Clear 969 + PushBuffer 706 = 3,927), the async read machinery totals ~0.82% of CPU."},{"method":"System.Text.UnicodeEncoding.GetCharCount + GetChars","inclusivePct":0.55,"exclusivePct":0.55,"callChain":["TdsParser.TryReadPlpUnicodeCharsChunk","UnicodeEncoding.GetCharCount","UnicodeEncoding.GetChars"],"observation":"4,222 samples decoding Unicode strings from SQL result columns. Combined with TryReadPlpUnicodeCharsChunk (1,865) and String.CreateStringFromEncoding (791), string materialization from SQL totals ~0.9%. Selecting only needed columns via projection would reduce this proportionally."},{"method":"Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync","inclusivePct":6.5,"exclusivePct":0.1,"callChain":["Controller Action","ToListAsync"],"observation":"ToListAsync is the top-level call driving all EF Core query execution. Its high inclusive % (all downstream SQL + materialization + tracking) vs near-zero exclusive % confirms it's purely a call-site. The real cost is what it triggers: unbounded result sets materialized with full change tracking."},{"method":"Microsoft.Extensions.DependencyInjection.ResolveService","inclusivePct":0.11,"exclusivePct":0.11,"callChain":["Kestrel Request Pipeline","DI Container","ResolveService"],"observation":"825 samples in DI resolution — minor but indicates per-request service construction. If DbContext or heavy services are transient, consider scoped lifetimes to reduce allocation and resolution overhead."}],"summary":"CPU time is dominated by SQL data reading and entity materialization — the TDS parser, Unicode decoding, and EF Core change tracking collectively account for the largest share of application-level CPU. The primary optimization targets are: (1) add .AsNoTracking() to read-only query paths to eliminate ~2,700 samples of change-tracking overhead and the associated SortedDictionary enumeration (~7,950 samples), (2) use .Select() projections to fetch only required columns, which would proportionally reduce TDS parsing, Unicode decoding, and JSON serialization costs, and (3) add pagination or result-set limits since the volume of per-row async overhead and string materialization suggests unbounded queries returning far more data than necessary. The 11.1% error rate combined with high SQL Server engine CPU (sqlmin at 7.6%) may indicate connection pool exhaustion or query timeouts under load."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":1.09,"gen1Rate":1.01,"gen2Rate":0.06,"pauseTimeMs":{"avg":9.96,"max":516.6,"total":2579.5},"gcPauseRatio":2.2,"fragmentationPct":0.0,"observations":["Gen1 count (121) is nearly equal to Gen0 count (131), meaning 92% of Gen0 collections promote survivors into Gen1 — objects are living just past Gen0 threshold, likely mid-request allocations held across await points","Max Gen0 pause of 516.6ms exceeds the p95 latency target (496.7ms) — a single GC pause is enough to blow out tail latency and likely explains a portion of the 11.11% error rate (request timeouts during GC)","Gen2 collections are low (7 total) indicating long-lived objects are well-managed; the problem is high-volume ephemeral allocations that survive into Gen1","GC pause ratio of 2.2% is below the 5% alarm threshold but the extreme max-pause variance (516.6ms vs 13.5ms avg) means GC impact is bursty, not steady — p95/p99 latency suffers even though average throughput looks acceptable"]},"heapAnalysis":{"peakSizeMB":2136.31,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 2.1GB is extremely large for an API workload at ~1295 req/sec — this suggests large per-request object graphs or unbounded caching/buffering","Total allocations of 93,498MB over ~120s yields ~779 MB/sec allocation rate, which is very high and is the root driver of frequent GC collections","Zero fragmentation indicates LOH is not a factor — pressure is purely from SOH (small object heap) churn","At 779 MB/sec allocation rate, the GC is forced to collect every ~1 second, and the sheer volume causes occasional compaction pauses exceeding 500ms"]},"topAllocators":[{"type":"(unavailable — no allocation type breakdown in input)","allocMB":null,"pctOfTotal":null,"callSite":null,"observation":"Allocation type data was not captured. Re-run PerfView with /DotNetAllocSampled and export 'GC Heap Alloc Stacks' to identify the dominant allocating types. Given 779 MB/sec, likely culprits are: (1) large response serialization buffers, (2) string allocations from query building or logging, (3) Entity Framework materialization creating excessive intermediate objects, (4) LINQ .ToList() or .Select() chains producing throwaway collections"}],"summary":"The API allocates ~779 MB/sec causing 259 GC collections in 120s, with Gen1 collecting nearly as often as Gen0 — meaning most objects survive just long enough to be promoted. The critical issue is a 516.6ms max Gen0 pause that directly causes tail-latency spikes and likely contributes to the 11.11% error rate via request timeouts. The #1 priority is reducing per-request allocation volume: investigate EF Core query materialization (use .AsNoTracking(), project to DTOs instead of full entities), eliminate intermediate collection allocations (replace .ToList().Where() chains with streaming IEnumerable), and consider object pooling (ArrayPool<T>, ObjectPool<T>) for any large buffers used in serialization or response writing. Reducing allocation rate by even 50% should halve GC frequency and eliminate the extreme pause spikes."}
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
