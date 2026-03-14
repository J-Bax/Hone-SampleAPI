Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 8)
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
| 6 | 2026-03-14 14:30 | `SampleApi/Pages/Products/Index.cshtml.cs` | Products page loads entire Products table for client-side pagination | regressed |
| 7 | 2026-03-14 14:55 | `SampleApi/Pages/Products/Detail.cshtml.cs` | Product detail OnPost loads entire CartItems table to find one row | regressed |


## Known Optimization Queue
- [TRIED] `SampleApi/Pages/Products/Index.cshtml.cs` — Products page loads entire Products table for client-side pagination *(experiment 6 — regressed)*
- [TRIED] `SampleApi/Pages/Products/Detail.cshtml.cs` — Product detail OnPost loads entire CartItems table to find one row *(experiment 7 — regressed)*
- [PENDING] [ARCHITECTURE] `SampleApi/Data/AppDbContext.cs` — No database indexes on frequently filtered columns

## Last Experiment's Fix
Product detail OnPost loads entire CartItems table to find one row

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


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":6.8,"exclusivePct":0.86,"callChain":["SqlDataReader.ReadAsync","TdsParser.TryRun","TdsParser.TryReadSqlValue","TdsParserStateObject.TryReadChar"],"observation":"Highest exclusive-sample application method. Character-level reading of SQL wire protocol dominates CPU — suggests queries return excessive column data or too many rows, amplifying per-byte parsing cost."},{"method":"Microsoft.Data.SqlClient.SqlDataReader (aggregate pipeline)","inclusivePct":12.5,"exclusivePct":2.5,"callChain":["EFCore.SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun","TryReadColumnInternal/TryReadSqlValue/TryProcessColumnHeaderNoNBC"],"observation":"TdsParser + SqlDataReader methods collectively account for ~12,200 exclusive samples. The profile is dominated by row-by-row column reading — classic sign of fetching too many columns (SELECT *) or materializing large result sets instead of projecting only needed fields."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1.MoveNextAsync","inclusivePct":15.0,"exclusivePct":0.14,"callChain":["ToListAsync.MoveNext","SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun"],"observation":"High inclusive but low exclusive — this is the call-site driving all SQL data reader work. Combined with ToListAsync (290 samples) and InternalEntityEntry ctor (272 samples), EF Core query materialization is the dominant application-level cost. Likely loading full entity graphs when projections (Select) would suffice."},{"method":"System.Text.Json.Serialization.Converters.StringConverter.Write","inclusivePct":1.2,"exclusivePct":0.35,"callChain":["ObjectDefaultConverter.OnTryWrite","StringConverter.Write","JsonWriterHelper.ToUtf8","OptimizedInboxTextEncoder.GetIndexOfFirstCharToEncodeSsse3"],"observation":"String serialization is the top JSON hotspot at 1,678 samples, with ObjectDefaultConverter.OnTryWrite (431) and ToUtf8 (443) adding up. Indicates responses contain many string properties — reducing response payload size or using source generators would help."},{"method":"System.Runtime.CompilerServices.CastHelpers (aggregate)","inclusivePct":1.4,"exclusivePct":1.36,"callChain":["EFCore materialization / DI resolution","CastHelpers.ChkCastAny/IsInstanceOfInterface/IsInstanceOfClass/ChkCastInterface"],"observation":"Type-checking and casting methods total ~6,625 exclusive samples (ChkCastAny 1599, IsInstanceOfInterface 1551, IsInstanceOfClass 1275, ChkCastInterface 1203, others). This reflects heavy polymorphic dispatch from EF Core materialization and DI — reducing entity complexity and avoiding deep inheritance would lower this overhead."},{"method":"System.Collections.Generic.SortedDictionary/SortedSet enumeration","inclusivePct":0.7,"exclusivePct":0.49,"callChain":["Application code or EF internals","SortedDictionary.ValueCollection.GetEnumerator","SortedSet.Enumerator.MoveNext","SortedDictionary.Enumerator.MoveNext"],"observation":"SortedDictionary/SortedSet enumeration totals ~2,357 exclusive samples. Tree-based collections have poor cache locality vs Dictionary/List. If application code uses SortedDictionary, switching to Dictionary + single Sort or using sorted database queries would eliminate this cost."},{"method":"Microsoft.Extensions.DependencyInjection.ResolveService","inclusivePct":0.5,"exclusivePct":0.08,"callChain":["Request pipeline","ResolveService","Dictionary.FindValue(ServiceCacheKey)"],"observation":"DI resolution (370 samples) plus ServiceCacheKey dictionary lookups (358 samples) suggest transient services resolved per-request. If heavy services are registered as Transient, switching to Scoped or Singleton would reduce allocation and lookup overhead."},{"method":"System.Threading.ExecutionContext.Capture / SetLocalValue","inclusivePct":0.6,"exclusivePct":0.29,"callChain":["AsyncMethodBuilderCore.Start","ExecutionContext.Capture","ExecutionContext.SetLocalValue","ExecutionContext.OnValuesChanged"],"observation":"Async state machine overhead: ExecutionContext.Capture (465), SetLocalValue (518), OnValuesChanged (395), AsyncMethodBuilderCore.Start (366). Total ~1,744 samples. Excessive async/await layering per request — consider reducing unnecessary async wrappers or using ValueTask where possible."},{"method":"Microsoft.EntityFrameworkCore.ChangeTracking.Internal.InternalEntityEntry..ctor","inclusivePct":0.3,"exclusivePct":0.06,"callChain":["SingleQueryingEnumerable.MoveNextAsync","StateManager.StartTrackingFromQuery","InternalEntityEntry..ctor"],"observation":"Entity change tracking construction at 272 samples. For read-only queries (GET endpoints), using AsNoTracking() would eliminate change tracker allocation entirely, reducing both CPU and memory pressure."},{"method":"SQL Server Engine (sqlmin + sqllang + sqldk + sqltses)","inclusivePct":8.4,"exclusivePct":8.4,"callChain":["TdsParser.TryRun","SQL Server network protocol","sqlmin/sqllang/sqldk query execution"],"observation":"SQL Server engine modules total ~40,722 exclusive samples — the largest single consumer. This is server-side query execution time. Combined with high client-side parsing, it strongly indicates queries are doing too much work: missing indexes, unfiltered scans, or N+1 patterns generating many round-trips."}],"summary":"The CPU profile is overwhelmingly dominated by SQL data access: SQL Server engine processing (~8.4%) and client-side TDS wire parsing (~2.5% exclusive) together consume the most application-relevant CPU. EF Core materializes large result sets through SingleQueryingEnumerable, driving heavy column-by-column parsing, type casting (~1.4%), and change tracking allocation. The 11.11% error rate combined with 482ms p95 latency suggests the database is under pressure — likely from unoptimized queries (missing projections, no AsNoTracking, possible N+1 patterns). The developer should: (1) add .Select() projections to avoid SELECT *, (2) use AsNoTracking() on read-only queries, (3) check for N+1 query patterns in EF Core, and (4) replace any SortedDictionary usage with Dictionary or push sorting to SQL. JSON serialization is a secondary concern that would shrink automatically with smaller response payloads."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.97,"gen1Rate":0.89,"gen2Rate":0.05,"pauseTimeMs":{"avg":84.7,"max":1931.4,"total":19242.1},"gcPauseRatio":16.2,"fragmentationPct":0.0,"observations":["GC pause ratio of 16.2% is critically high (threshold: 5%) — the application spends roughly 1 in 6 seconds frozen in GC, directly degrading throughput and latency","Gen0 average pause of 124.2ms is ~100x what a healthy Gen0 collection should take (<1ms), indicating extreme memory pressure forcing expensive ephemeral-segment compaction or induced full-blocking collections","Gen1 average pause of 46.7ms with 106 collections is abnormally high — nearly every Gen0 collection escalates to Gen1, meaning objects survive Gen0 at an alarming rate, likely due to mid-request allocations that outlive the ephemeral segment budget","Max pause of 1931.4ms (nearly 2 seconds) is catastrophic — this single GC event alone exceeds the p95 latency target by 4x and is almost certainly responsible for the 11.11% error rate via request timeouts","Gen2 collections are infrequent (6) and fast (avg 2.1ms), indicating background GC is handling the tenured heap well — the crisis is entirely in the ephemeral generations","The near-1:1 ratio of Gen0 (115) to Gen1 (106) collections means 92% of Gen0 collections promote objects to Gen1, suggesting massive mid-request allocation bursts that cannot be reclaimed quickly enough"]},"heapAnalysis":{"peakSizeMB":2068.01,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 2068MB (~2GB) under load is extremely high for an API service — this suggests large object graphs, unbounded caching, or per-request allocations that accumulate faster than GC can reclaim","Total allocation volume of 79,007MB (~79GB) over ~119 seconds yields an allocation rate of ~664 MB/sec — this is an extraordinary allocation rate that overwhelms the GC subsystem","At 664 MB/sec allocation rate with 1321 RPS, each request allocates roughly 515KB on average — look for large buffer allocations, serialization overhead, or unbounded LINQ materializations per request","Zero fragmentation is a positive signal — LOH compaction is not an issue, and the problem is purely allocation volume in the small object heap"]},"topAllocators":[{"type":"(allocation type data unavailable)","allocMB":null,"pctOfTotal":null,"callSite":"Unknown — PerfView allocation tick data was not captured or export failed","observation":"Re-run diagnostics with /DotNetAllocSampled and export the 'GC Heap Alloc Stacks' view to identify the specific types and call sites driving the 79GB allocation volume. Without this data, focus on the patterns below."}],"likelyAllocationPatterns":[{"pattern":"Unbounded LINQ materialization (ToList/ToArray on large query results)","evidence":"515KB average per-request allocation and 2GB peak heap suggest large collections being fully materialized into memory rather than streamed","fix":"Use IAsyncEnumerable or pagination to avoid materializing entire result sets; apply .Take(N) before .ToListAsync()"},{"pattern":"String-heavy serialization or response building","evidence":"High allocation rate with rapid Gen0-to-Gen1 promotion suggests objects that outlive a single GC cycle, typical of serialization buffers","fix":"Use System.Text.Json source generators, pool serialization buffers via ArrayPool<byte>, avoid intermediate string representations"},{"pattern":"Entity Framework change tracker bloat","evidence":"Peak heap of 2GB and high Gen1 survival rate suggest tracked entities accumulating across the request lifetime","fix":"Use AsNoTracking() for read-only queries; ensure DbContext is scoped per-request and disposed promptly"},{"pattern":"Large byte[] or string allocations hitting SOH","evidence":"664 MB/sec allocation rate is consistent with frequent buffer allocations just under the 85KB LOH threshold","fix":"Use ArrayPool<T>.Shared.Rent/Return for temporary buffers; use Memory<T>/Span<T> to avoid copying"}],"summary":"The application is in a GC crisis: 16.2% of execution time is spent in garbage collection, with a catastrophic max pause of 1.9 seconds causing the 11.11% error rate and inflating p95 latency to 482ms. The root cause is an extreme allocation rate of ~664 MB/sec (79GB total), averaging 515KB per request, which overwhelms the ephemeral GC generations — 92% of Gen0 collections escalate to Gen1. The #1 priority is to identify and eliminate the largest per-request allocations, most likely unbounded query materializations (ToList on large result sets) or unpoooled buffer allocations. Re-enable PerfView allocation sampling to pinpoint exact types, but in the meantime, audit all controller actions for .ToList()/.ToArray() on potentially large collections and replace with streamed or paginated alternatives."}
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
