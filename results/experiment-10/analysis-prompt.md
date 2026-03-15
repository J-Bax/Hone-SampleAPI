Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 10)
- p95 Latency: 531.09704ms
- Requests/sec: 1195.2
- Error rate: 11.11%
- Improvement vs baseline: 66.7%

## Baseline Performance
- p95 Latency: 1596.242785ms
- Requests/sec: 468.5
- Error rate: 11.11%

## Runtime Counters
- CPU avg: 25.91%
- GC heap max: 2481MB
- Gen2 collections: 22987544
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
| 1 | 2026-03-15 04:10 | `SampleApi/Controllers/ProductsController.cs` | Full table scans with in-memory filtering on Products | improved |
| 2 | 2026-03-15 04:36 | `SampleApi/Controllers/CartController.cs` | N+1 queries, full table scans, and per-item SaveChanges in Cart | improved |
| 3 | 2026-03-15 05:01 | `SampleApi/Controllers/ReviewsController.cs` | Full table scan of ~2000 Reviews with in-memory filtering | improved |
| 4 | 2026-03-15 05:40 | `SampleApi/Pages/Checkout/Index.cshtml.cs` | Per-item SaveChanges and full table scans in Checkout | improved |
| 5 | 2026-03-15 06:05 | `SampleApi/Pages/Products/Detail.cshtml.cs` | Full table scans of Reviews and Products in product detail page | regressed |
| 6 | 2026-03-15 06:30 | `SampleApi/Pages/Orders/Index.cshtml.cs` | Triple full table scan with N+1 on growing tables in order history | improved |
| 7 | 2026-03-15 07:07 | `SampleApi/Pages/Index.cshtml.cs` | Full table scans of Products and Reviews on home page | improved |
| 8 | 2026-03-15 07:32 | `SampleApi/Pages/Cart/Index.cshtml.cs` | Full CartItems table scan with N+1 lookups and per-item SaveChanges | improved |
| 9 | 2026-03-15 07:57 | `SampleApi/Pages/Products/Index.cshtml.cs` | Full Products table scan with in-memory filtering and no AsNoTracking | improved |


## Known Optimization Queue
- [TRIED] `SampleApi/Pages/Index.cshtml.cs` — Full table scans of Products and Reviews on home page *(experiment 7 — improved)*
- [TRIED] `SampleApi/Pages/Cart/Index.cshtml.cs` — Full CartItems table scan with N+1 lookups and per-item SaveChanges *(experiment 8 — improved)*
- [TRIED] `SampleApi/Pages/Products/Index.cshtml.cs` — Full Products table scan with in-memory filtering and no AsNoTracking *(experiment 9 — improved)*

## Last Experiment's Fix
Full Products table scan with in-memory filtering and no AsNoTracking

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
| 7 | — | improved | 543 | 1164.1 | hone/experiment-7 |
| 8 | — | improved | 546.9 | 1177.8 | hone/experiment-8 |
| 9 | — | improved | 531.1 | 1195.2 | hone/experiment-9 |


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":1.85,"exclusivePct":1.85,"callChain":["EFCore.SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun","TdsParserStateObject.TryReadChar"],"observation":"Highest exclusive-time application-layer method. Combined with TryReadColumnInternal (4449), TryReadSqlValue (1467), TryReadSqlStringValue (693), GetString (514), and numerous TdsParser methods totaling ~30K+ samples, this indicates the API is fetching very large result sets from SQL Server — likely over-fetching columns or rows."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1+AsyncEnumerator.MoveNextAsync","inclusivePct":0.5,"exclusivePct":0.5,"callChain":["EFCore.ToListAsync","SingleQueryingEnumerable.AsyncEnumerator.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun"],"observation":"EF Core async enumeration driving the entire SQL read pipeline. Combined with ToListAsync (714 samples), this suggests queries are materializing large collections into memory. Consider adding pagination, projection (Select), or filtering to reduce row counts."},{"method":"System.Collections.Generic.SortedDictionary/SortedSet enumeration","inclusivePct":1.31,"exclusivePct":1.31,"callChain":["Application Code","SortedDictionary.ValueCollection.GetEnumerator","SortedSet.Enumerator.MoveNext"],"observation":"SortedDictionary/SortedSet enumeration consumes ~6255 samples across 6 methods (MoveNext: 1399+1262+868, get_Current: 870, GetEnumerator: 821+505, Initialize: 530). SortedDictionary has O(log n) access and poor cache locality. If sort order isn't required per-request, switch to Dictionary<TKey,TValue> or return pre-sorted results from SQL via ORDER BY."},{"method":"System.Text.Json.Serialization.Converters.StringConverter.Write","inclusivePct":0.63,"exclusivePct":0.63,"callChain":["ObjectDefaultConverter.OnTryWrite","StringConverter.Write","JsonWriterHelper.ToUtf8","OptimizedInboxTextEncoder.GetIndexOfFirstCharToEncodeSsse3"],"observation":"JSON string serialization totals ~5886 samples (StringConverter.Write: 3022, ToUtf8: 995, ObjectDefaultConverter.OnTryWrite: 968, text encoder: 901). This indicates responses contain many string properties. Consider DTO projection to return only needed fields, or enable response compression to reduce serialization overhead for large payloads."},{"method":"System.Runtime.CompilerServices.CastHelpers (aggregate)","inclusivePct":2.8,"exclusivePct":2.8,"callChain":["EFCore materialization / LINQ enumeration","CastHelpers.IsInstanceOfClass/Interface/ChkCast*"],"observation":"Type-checking and casting consumes ~13315 samples across 7 CastHelpers methods (IsInstanceOfClass: 3165, IsInstanceOfInterface: 3009, ChkCastAny: 2603, ChkCastInterface: 2446, IsInstanceOfAny: 703, StelemRef: 780+609). This is a symptom of heavy polymorphic dispatch during EF Core materialization and LINQ operations over large collections — reducing the number of entities materialized will reduce this proportionally."},{"method":"System.Text.UnicodeEncoding.GetCharCount + GetChars","inclusivePct":1.84,"exclusivePct":1.84,"callChain":["SqlDataReader.TryReadColumnInternal","TdsParser.TryReadSqlStringValue","UnicodeEncoding.GetCharCount/GetChars","String.CreateStringFromEncoding"],"observation":"Unicode string decoding from SQL wire protocol totals ~9554 samples (GetCharCount: 6388, GetChars: 2368, CreateStringFromEncoding: 798). This confirms the API reads a very high volume of string data from SQL Server. Reducing selected columns (especially large nvarchar fields) or using projection would directly cut this cost."},{"method":"System.Threading.ExecutionContext (async overhead)","inclusivePct":1.0,"exclusivePct":1.0,"callChain":["AsyncMethodBuilderCore.Start","ExecutionContext.Capture","ExecutionContext.SetLocalValue","ExecutionContext.OnValuesChanged"],"observation":"Async machinery totals ~4777 samples (Capture: 1281, SetLocalValue: 1030, OnValuesChanged: 1011, AsyncMethodBuilderCore.Start: 904, ConfiguredCancelableAsyncEnumerable.MoveNextAsync: 551). High async overhead relative to useful work suggests many small async operations — likely per-row async reads. Batching or reducing row counts would amortize this cost."},{"method":"System.Collections.Generic.Dictionary`2.FindValue / TryInsert","inclusivePct":0.87,"exclusivePct":0.87,"callChain":["EFCore materialization / DI resolution","Dictionary.FindValue","Dictionary.TryInsert"],"observation":"Dictionary operations total ~4116 samples (FindValue generic: 2094, TryInsert: 741, FindValue ServiceCacheKey: 657, FindValue Int32: 624). The generic FindValue likely serves EF Core's identity map or change tracker. Disabling change tracking (AsNoTracking) for read-only queries would reduce this significantly."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.PrepareAsyncInvocation + SetSnapshot","inclusivePct":0.82,"exclusivePct":0.82,"callChain":["SqlDataReader.ReadAsync","PrepareAsyncInvocation","TdsParserStateObject.SetSnapshot/StateSnapshot.Snap/Clear/PushBuffer"],"observation":"Async read preparation totals ~3899 samples (PrepareAsyncInvocation: 2852, SetSnapshot: 1061, Snap: 1739, Clear: 995, PushBuffer: 804). These create state snapshots for every async read call. The overhead is proportional to the number of rows read — strong evidence of over-fetching from the database."},{"method":"Microsoft.Extensions.DependencyInjection.ResolveService","inclusivePct":0.17,"exclusivePct":0.17,"callChain":["Request pipeline","DI Container","ResolveService"],"observation":"DI resolution (804 samples) plus DefaultBinder.SelectMethod (578) suggests per-request service resolution overhead. Consider registering frequently-resolved services as singletons where thread-safe, or caching resolved instances within request scope."}],"summary":"The CPU profile is dominated by SQL data reading and materialization: TdsParser/SqlDataReader methods account for ~30K+ application-level samples, with heavy UnicodeEncoding string decoding (~9.5K) and EF Core enumeration driving it all. The most actionable optimizations are: (1) Add query projection (.Select) and pagination to reduce rows/columns fetched — this will cut SQL reading, string decoding, async overhead, and type casting proportionally; (2) Use .AsNoTracking() for read-only queries to eliminate change-tracker Dictionary overhead; (3) Replace SortedDictionary with Dictionary or push sorting to SQL ORDER BY — the ~6.2K samples in SortedSet enumeration suggest unnecessary in-memory sorting. The 11.1% error rate combined with high p95 latency (531ms) suggests the server is under memory/CPU pressure from over-fetching, likely causing request timeouts under load."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.06,"gen1Rate":0.28,"gen2Rate":0.38,"pauseTimeMs":{"avg":79.7,"max":210.0,"total":6930.9},"gcPauseRatio":5.8,"fragmentationPct":0.0,"observations":["GC generation distribution is severely inverted: Gen2 (46) > Gen1 (34) > Gen0 (7). Normal is Gen0 >> Gen1 >> Gen2. This indicates objects are surviving through younger generations rapidly or large object heap (LOH) allocations are triggering full Gen2 collections directly.","GC pause ratio of 5.8% exceeds the 5% concern threshold — the application is spending nearly 7 seconds in GC pauses over the ~120s test, directly stealing CPU time from request processing.","Max GC pause of 210ms (Gen0) is extremely high and directly contributes to the 531ms p95 latency. Gen0 collections should be sub-millisecond; 110ms average suggests the managed heap is so large that root scanning and compaction are expensive even for ephemeral collections.","Gen1 pauses averaging 109.7ms with a 204.2ms max are also pathological — Gen1 should typically complete in under 10ms. This strongly suggests the ephemeral segment is oversized due to massive allocation throughput.","Gen2 collections averaging 52.8ms are relatively fast for full collections, but occurring 46 times in 120s means one every ~2.6 seconds — an extremely aggressive Gen2 collection rate that indicates chronic memory pressure."]},"heapAnalysis":{"peakSizeMB":2794.07,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 2.8GB under load is extremely large for an API service. This level of memory consumption puts heavy pressure on the GC and likely causes the OS to page, contributing to the high error rate (11.11%).","Total allocations of 101.6GB over ~120s yields an allocation rate of ~846 MB/sec. This is an extraordinary allocation throughput that forces the GC into continuous collection cycles.","Fragmentation at 0% rules out LOH fragmentation as a concern, but the sheer volume of allocations and peak heap size point to objects either being too large or held too long before becoming eligible for collection.","The 11.11% error rate is likely caused by GC-induced request timeouts or out-of-memory conditions when the heap approaches its peak — consider whether requests are queuing behind 210ms stop-the-world pauses."]},"topAllocators":[{"type":"(allocation type data unavailable)","allocMB":null,"pctOfTotal":null,"callSite":"unknown — PerfView allocation tick data was not captured or export failed","observation":"Re-run with PerfView /DotNetAllocSampled and export GC Heap Alloc Stacks to identify which types and call sites are responsible for the 846 MB/sec allocation rate. Without this data, focus on the code paths that handle the highest-volume endpoints."}],"summary":"This API has a critical memory throughput problem: allocating ~846 MB/sec and peaking at 2.8GB heap, causing an inverted GC generation pattern where Gen2 collections (46) outnumber Gen0 (7). The 210ms max GC pause directly inflates p95 latency, and the 5.8% pause ratio means the GC is competing with request processing for CPU. The #1 priority is reducing allocation volume — likely large per-request object graphs, unbounded query result materialization, or missing object pooling. Profile with /DotNetAllocSampled to pinpoint the top allocating call sites, then apply object reuse (ArrayPool<T>, ObjectPool), streaming/pagination to avoid materializing large result sets, and caching of repeated computations. Reducing allocations by even 50% should dramatically cut Gen2 frequency, lower pause times, and recover the lost throughput currently spent on garbage collection."}
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
