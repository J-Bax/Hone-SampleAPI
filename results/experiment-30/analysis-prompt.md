Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 30)
- p95 Latency: 534.5337ms
- Requests/sec: 1101.5
- Error rate: 0%
- Improvement vs baseline: 92.9%

## Baseline Performance
- p95 Latency: 7546.103045ms
- Requests/sec: 125.5
- Error rate: 0%

## Runtime Counters
- CPU avg: 13.14%
- GC heap max: 838MB
- Gen2 collections: 0
- Thread pool max threads: 39

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
| 7 | 2026-03-15 15:54 | `SampleApi/Pages/Cart/Index.cshtml.cs` | Eliminate full CartItems table scan and N+1 product lookups in Cart page LoadCart | improved |
| 8 | 2026-03-15 16:19 | `SampleApi/Pages/Index.cshtml.cs` | Replace dual full-table scans with targeted queries on Home page | improved |
| 9 | 2026-03-15 16:44 | `SampleApi/Pages/Products/Index.cshtml.cs` | Replace full product table scan with server-side filtering and pagination | improved |
| 10 | 2026-03-15 17:26 | `SampleApi/Controllers/OrdersController.cs` | Batch product lookups and add AsNoTracking in CreateOrder | improved |
| 11 | 2026-03-15 17:56 | `SampleApi/Pages/Checkout/Index.cshtml.cs` | Consolidate redundant SaveChangesAsync round trips in checkout post | improved |
| 12 | 2026-03-15 18:35 | `SampleApi/Controllers/ReviewsController.cs` | Consolidate redundant DB round trips in review endpoints | improved |
| 13 | 2026-03-15 18:59 | `SampleApi/Controllers/CartController.cs` | Add AsNoTracking to read-only cart and product queries | improved |
| 14 | 2026-03-15 19:24 | `SampleApi/Controllers/OrdersController.cs` | Replace full table scans and N+1 queries in order read endpoints | stale |
| 15 | 2026-03-15 20:04 | `SampleApi/Pages/Products/Detail.cshtml.cs` | Add AsNoTracking to all read-only queries on Detail page | improved |
| 16 | 2026-03-15 20:52 | `SampleApi/Data/AppDbContext.cs` | Add database indexes on high-traffic filter columns | improved |
| 17 | 2026-03-15 21:17 | `SampleApi/Pages/Checkout/Index.cshtml.cs` | Add AsNoTracking and Select projections to Checkout read queries | improved |
| 18 | 2026-03-15 21:40 | `SampleApi/Controllers/ProductsController.cs` | Test failure: Add pagination and DTO projection to product list endpoints | regressed |
| 19 | 2026-03-15 22:05 | `SampleApi/Controllers/ReviewsController.cs` | Eliminate redundant product existence DB round trips in review endpoints | improved |
| 20 | 2026-03-15 22:30 | `SampleApi/Controllers/OrdersController.cs` | Add AsNoTracking and server-side filtering to GetOrder with batched product lookup | improved |
| 21 | 2026-03-15 23:06 | `SampleApi/Controllers/ProductsController.cs` | Eliminate redundant category existence DB round trip in GetProductsByCategory | improved |
| 22 | 2026-03-15 23:31 | `SampleApi/Pages/Cart/Index.cshtml.cs` | Add Select projection to product lookup in Cart page LoadCart | improved |
| 23 | 2026-03-15 23:55 | `SampleApi/Pages/Orders/Index.cshtml.cs` | Add Select projection to product name lookup in Orders page | improved |
| 24 | 2026-03-16 00:09 | `SampleApi/Program.cs` | Test failure: Replace AddDbContext with AddDbContextPool to reduce allocation pressure | regressed |
| 25 | 2026-03-16 00:33 | `SampleApi/Pages/Index.cshtml.cs` | Replace NEWID() random ordering with efficient deterministic query for featured products | stale |
| 26 | 2026-03-16 08:59 | `SampleApi/Controllers/CartController.cs` | Add Select projection to product dictionary lookup in GetCart API endpoint | improved |
| 27 | 2026-03-16 09:45 | `SampleApi/Controllers/ProductsController.cs` | Add Select projection excluding Description on product list endpoints and AsNoTracking on GetProduct | improved |
| 28 | 2026-03-16 10:09 | `SampleApi/Controllers/CartController.cs` | Replace FindAsync with AnyAsync for product existence check in AddToCart | stale |
| 29 | 2026-03-16 10:34 | `SampleApi/Controllers/OrdersController.cs` | Add Select projection to product lookup in CreateOrder | improved |


## Known Optimization Queue
- [TRIED] `SampleApi/Controllers/ProductsController.cs` — Add Select projection excluding Description on product list endpoints and AsNoTracking on GetProduct *(experiment 27 — improved)*
- [TRIED] `SampleApi/Controllers/CartController.cs` — Replace FindAsync with AnyAsync for product existence check in AddToCart *(experiment 28 — stale)*
- [TRIED] `SampleApi/Controllers/OrdersController.cs` — Add Select projection to product lookup in CreateOrder *(experiment 29 — improved)*

## Last Experiment's Fix
Add Select projection to product lookup in CreateOrder

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
| 7 | — | improved | 677.4 | 872.1 | hone/experiment-7 |
| 8 | — | improved | 606.9 | 976.2 | hone/experiment-8 |
| 9 | — | improved | 591.9 | 980.9 | hone/experiment-9 |
| 10 | — | improved | 540.5 | 1033.1 | hone/experiment-10 |
| 11 | — | improved | 548.4 | 1006.2 | hone/experiment-11 |
| 12 | — | improved | 546.3 | 1028.9 | hone/experiment-12 |
| 13 | — | improved | 548.8 | 1032.4 | hone/experiment-13 |
| 14 | — | stale | 551.6 | 1025.2 | hone/experiment-14 |
| 15 | — | improved | 546 | 1025.7 | hone/experiment-15 |
| 16 | — | improved | 595.7 | 698.5 | hone/experiment-16 |
| 17 | — | improved | 536 | 1052.1 | hone/experiment-17 |
| 18 | — | test_failure | N/A | N/A | hone/experiment-18 |
| 19 | — | improved | 537.1 | 1055.8 | hone/experiment-19 |
| 20 | — | improved | 544.8 | 1078.1 | hone/experiment-20 |
| 21 | — | improved | 535.2 | 1093.7 | hone/experiment-21 |
| 22 | — | improved | 535.1 | 1091.5 | hone/experiment-22 |
| 23 | — | improved | 546.1 | 1100.1 | hone/experiment-23 |
| 24 | — | test_failure | N/A | N/A | hone/experiment-24 |
| 25 | — | stale | 548.4 | 1060.2 | hone/experiment-25 |
| 26 | — | improved | 544.7 | 1087.3 | hone/experiment-26 |
| 27 | — | improved | 535.2 | 1123.3 | hone/experiment-27 |
| 28 | — | stale | 541.6 | 1090.9 | hone/experiment-28 |
| 29 | — | improved | 534.5 | 1101.5 | hone/experiment-29 |


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.SqlDataReader.TryReadColumnInternal","inclusivePct":3.6,"exclusivePct":0.61,"callChain":["ToListAsync","SingleQueryingEnumerable.MoveNextAsync","ExecuteReaderAsync","TdsParser.TryRun","SqlDataReader.TryReadColumnInternal"],"observation":"Heaviest application-layer leaf frame in the SQL stack. Combined with TdsParser.TryGetTokenLength (1010), WillHaveEnoughData (756), TryRun (699), TryProcessColumnHeaderNoNBC (597), and numerous TryRead* methods, the SQL data-reading pipeline accounts for ~8,500 exclusive samples (~3.6%). This volume of column-level parsing strongly suggests queries are returning too many rows or too many columns — likely missing pagination, over-fetching with SELECT *, or N+1 query patterns."},{"method":"System.Text.Json.Serialization.Converters.ObjectDefaultConverter`1.OnTryWrite","inclusivePct":1.9,"exclusivePct":0.44,"callChain":["Controller.Action","JsonSerializer.Serialize","ObjectDefaultConverter.OnTryWrite","JsonPropertyInfo.GetMemberAndWriteJson","Utf8JsonWriter.WriteStringMinimized"],"observation":"JSON serialization accounts for ~4,500 exclusive samples (~1.9%) across OnTryWrite, WriteStack.Push/Pop, WriteStringMinimized, WriteNullSection, WritePropertyNameSection, StringConverter.Write, and text encoding. Large response payloads with many string properties are being serialized. Reducing the number of entities returned (pagination) or projecting to smaller DTOs would directly reduce this cost."},{"method":"System.Runtime.CompilerServices.CastHelpers.IsInstanceOfInterface","inclusivePct":3.3,"exclusivePct":1.02,"callChain":["Various EF Core / DI / MVC pipeline paths","CastHelpers.IsInstanceOfInterface"],"observation":"Type-casting helpers (IsInstanceOfInterface 2379, IsInstanceOfClass 1979, StelemRef_Helper 886, ChkCastAny 760, IsInstanceOfAny 683) total ~7,800 samples (~3.3%). This extreme interface-dispatch overhead is proportional to the number of objects being processed — materializing thousands of EF entities through interfaces amplifies this cost. Reducing result set sizes will proportionally reduce casting overhead."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1+AsyncEnumerator.MoveNextAsync","inclusivePct":0.7,"exclusivePct":0.31,"callChain":["ToListAsync","ConfiguredCancelableAsyncEnumerable.MoveNextAsync","SingleQueryingEnumerable.MoveNextAsync","ExecuteReaderAsync"],"observation":"EF Core enumeration (MoveNextAsync 733, ToListAsync 279, ExecuteReaderAsync 314) shows the ORM is iterating through large result sets. The SingleQueryingEnumerable pattern plus the massive SQL reader overhead below it confirms queries are materializing many rows. Adding .Take()/.Skip() pagination, .AsNoTracking(), or projecting with .Select() to limit columns would reduce both EF and SQL overhead."},{"method":"Microsoft.Extensions.DependencyInjection.ServiceProvider.GetService / ResolveService","inclusivePct":1.3,"exclusivePct":0.51,"callChain":["Controller constructor or middleware","ServiceProvider.GetService","dynamicClass.ResolveService","Dictionary.FindValue(ServiceCacheKey)"],"observation":"DI resolution (ResolveService 908, ServiceCacheKey Dictionary.FindValue 712, GetService 276, Dictionary.TryInsert 294) totals ~3,000 samples (~1.3%). This suggests either many transient services being resolved per request, or services resolved inside loops. Consider promoting hot-path services to scoped/singleton lifetime or injecting IServiceScopeFactory only where needed."},{"method":"System.Text.UnicodeEncoding.GetCharCount / GetChars","inclusivePct":1.5,"exclusivePct":1.14,"callChain":["SqlDataReader.TryReadColumnInternal","TdsParser.TryReadSqlStringValue","UnicodeEncoding.GetCharCount","UnicodeEncoding.GetChars"],"observation":"Unicode string decoding (GetCharCount 1575, GetChars 1096, CreateStringFromEncoding 310) totals ~3,000 exclusive samples. This is the cost of converting SQL Server's UTF-16 wire format into .NET strings for every string column in every row. The volume confirms many string-heavy rows are being read. Fetching fewer rows or fewer string columns directly reduces this cost."},{"method":"System.Reflection (DefaultBinder.SelectMethod / RuntimeType.GetMethodImplCommon)","inclusivePct":0.8,"exclusivePct":0.64,"callChain":["EF Core model building or MVC action selection","RuntimeType.GetMethodImplCommon","DefaultBinder.SelectMethod","FilterApplyMethodBase"],"observation":"Reflection methods (SelectMethod 569, GetMethodImplCommon 426, CerHashtable.get_Item 463, FilterApplyMethodBase 214, GetParametersCached 241) total ~1,900 samples. This level of reflection at runtime suggests either repeated model metadata lookups or dynamic method resolution on each request. Caching compiled expressions or ensuring EF model is fully compiled at startup would help."},{"method":"System.Threading.SemaphoreSlim.Wait (synchronous)","inclusivePct":0.13,"exclusivePct":0.13,"callChain":["Possibly DbContext pool or connection pool","SemaphoreSlim.Wait"],"observation":"SemaphoreSlim.Wait (293 samples) is a synchronous blocking call appearing in a profile that should be fully async. Combined with SpinWait.SpinOnceCore (304), this indicates thread-pool threads are being blocked — likely by connection pool contention or a synchronous-over-async anti-pattern. This directly increases latency under load by exhausting thread-pool threads."},{"method":"Microsoft.AspNetCore.Mvc.ViewFeatures.Buffers.ViewBuffer.WriteToAsync","inclusivePct":0.15,"exclusivePct":0.15,"callChain":["MVC pipeline","ViewResult.ExecuteResultAsync","ViewBuffer.WriteToAsync"],"observation":"ViewBuffer.WriteToAsync (339 samples) indicates MVC Razor views are being rendered. For a Web API serving JSON, this is unexpected overhead. If any endpoints return ViewResult instead of JsonResult/Ok(), switching them to return JSON directly eliminates view rendering cost entirely."},{"method":"System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start","inclusivePct":1.3,"exclusivePct":0.35,"callChain":["Every async method entry","AsyncMethodBuilderCore.Start","ExecutionContext.OnValuesChanged"],"observation":"Async machinery (Start 821, ExecutionContext.OnValuesChanged 559, SetLocalValue 419, RunInternal 330, ScheduleAndStart 221, RunOrScheduleAction 202) totals ~3,100 samples. While individual async overhead is unavoidable, the aggregate cost scales with the number of async calls — which is amplified by iterating large result sets. Reducing iterations (pagination, batching) reduces async overhead proportionally."}],"summary":"The CPU profile is dominated by SQL data reading and TDS wire-protocol parsing (~3.6% exclusive), with massive supporting costs in Unicode string decoding (~1.5%), JSON serialization (~1.9%), and type-casting overhead (~3.3%) — all scaling linearly with the number of rows and columns returned. The most actionable optimization is to add server-side pagination (.Skip/.Take) and column projection (.Select with DTOs) to EF Core queries, which would simultaneously reduce SQL parsing, string allocation, JSON serialization, type-casting, DI resolution, and async overhead. Secondary concerns include the synchronous SemaphoreSlim.Wait blocking threads under load, unexpected Razor view rendering in an API context, and per-request reflection that suggests missing caching of compiled metadata."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.75,"gen1Rate":0.61,"gen2Rate":0.04,"pauseTimeMs":{"avg":5.0,"max":38.9,"total":779.8},"gcPauseRatio":0.7,"fragmentationPct":0.0,"observations":["Gen1/Gen0 ratio is 82% (68/83) — an abnormally high promotion rate indicating objects consistently survive Gen0 but die in Gen1. This points to mid-lived allocations such as request-scoped buffers, intermediate collections, or EF change-tracker entries that outlive the Gen0 threshold.","Gen0 collection rate is modest (0.75/sec) but total allocation volume is extreme (32.8 GB over ~111s ≈ 295 MB/sec), meaning each Gen0 collection reclaims a very large nursery — the GC is doing heavy work per collection.","Gen2 collections are minimal (4 total, avg 3.3ms) — long-lived objects and LOH are well-managed. No background GC pressure.","Max pause of 38.9ms (Gen1) is below the 50ms concern threshold but could still contribute to p95 tail latency when it coincides with request processing. With p95 at 534ms, GC pauses are not the primary latency driver.","GC pause ratio of 0.7% is healthy — the application spends 99.3% of its time executing user code, so GC overhead is not a throughput bottleneck."]},"heapAnalysis":{"peakSizeMB":863.65,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 863 MB is high for an API workload — combined with 32.8 GB total allocations, the working set is large and the churn is enormous. Objects are being allocated and discarded at ~295 MB/sec.","Zero fragmentation indicates LOH is not a concern and the GC is compacting efficiently. No pinned buffer issues detected.","The ratio of total allocations (32,869 MB) to peak heap (863 MB) is ~38:1, meaning objects are short-to-mid-lived but the sheer volume drives GC work. Reducing allocation volume is the highest-leverage improvement."]},"topAllocators":[{"type":"(allocation type data not captured)","allocMB":null,"pctOfTotal":null,"callSite":"unknown","observation":"Allocation tick sampling was not captured (topTypes is empty, allocRateMBSec is 0.0). Re-run PerfView with /DotNetAllocSampled and export GC Heap Alloc Stacks to identify the specific types and call sites responsible for the 295 MB/sec allocation rate. Without this data, focus optimization on the patterns below inferred from GC behavior."},{"type":"(inferred) EF Core change-tracker and query materialization objects","allocMB":null,"pctOfTotal":null,"callSite":"likely DbContext query execution paths","observation":"The high Gen1 promotion rate strongly suggests request-scoped objects that live through at least one Gen0 collection. EF Core's change tracker, query result materializers, and DbConnection/DbCommand objects fit this profile. Use AsNoTracking() for read-only queries and consider object pooling for DbContext (AddDbContextPool)."},{"type":"(inferred) byte[]/string allocations from serialization","allocMB":null,"pctOfTotal":null,"callSite":"likely JSON serialization / response writing","observation":"At 295 MB/sec, large buffer allocations from JSON serialization or response body writing are a likely contributor. Use System.Text.Json source generators, IBufferWriter<byte>, or RecyclableMemoryStream to reduce allocation pressure in serialization hot paths."}],"summary":"The API allocates ~295 MB/sec with an abnormally high Gen1 promotion rate (82%), indicating mid-lived, request-scoped objects dominate the allocation profile — likely EF Core materialization and serialization buffers. While GC pause ratio (0.7%) is healthy and not causing the 534ms p95 latency directly, the massive allocation volume forces frequent large Gen0/Gen1 collections that add CPU overhead and cache pressure. The #1 fix is to enable allocation tick profiling to identify the exact hotspot types, then apply AsNoTracking for read queries, DbContext pooling, and buffer recycling to cut the 32.8 GB total allocation volume."}
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
