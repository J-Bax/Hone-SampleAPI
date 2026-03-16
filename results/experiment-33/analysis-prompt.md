Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 33)
- p95 Latency: 537.728965ms
- Requests/sec: 1124.5
- Error rate: 0%
- Improvement vs baseline: 92.9%

## Baseline Performance
- p95 Latency: 7546.103045ms
- Requests/sec: 125.5
- Error rate: 0%

## Runtime Counters
- CPU avg: 13.28%
- GC heap max: 851MB
- Gen2 collections: 33268752
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
| 30 | 2026-03-16 10:50 | `SampleApi/Pages/Products/Index.cshtml.cs` | Build failure: Add Select projection to paginated product query excluding Description | regressed |
| 31 | 2026-03-16 11:15 | `SampleApi/Controllers/CartController.cs` | Replace materialize-then-remove with raw SQL DELETE in ClearCart | improved |
| 32 | 2026-03-16 11:39 | `SampleApi/Pages/Index.cshtml.cs` | Add Select projection to FeaturedProducts and RecentReviews excluding large text fields | improved |


## Known Optimization Queue
- [TRIED] `SampleApi/Pages/Products/Index.cshtml.cs` — Add Select projection to paginated product query excluding Description *(experiment 30 — regressed)*
- [TRIED] `SampleApi/Controllers/CartController.cs` — Replace materialize-then-remove with raw SQL DELETE in ClearCart *(experiment 31 — improved)*
- [TRIED] `SampleApi/Pages/Index.cshtml.cs` — Add Select projection to FeaturedProducts and RecentReviews excluding large text fields *(experiment 32 — improved)*

## Last Experiment's Fix
Add Select projection to FeaturedProducts and RecentReviews excluding large text fields

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
| 30 | — | build_failure | N/A | N/A | hone/experiment-30 |
| 31 | — | improved | 545.1 | 1127.8 | hone/experiment-31 |
| 32 | — | improved | 537.7 | 1124.5 | hone/experiment-32 |


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync","inclusivePct":5.5,"exclusivePct":0.14,"callChain":["Controller Action","ToListAsync","MoveNextAsync","ExecuteReaderAsync","TdsParser.TryRun","SqlDataReader.TryReadColumnInternal"],"observation":"ToListAsync materializes entire result sets into memory. The massive SQL data reader overhead beneath it (11,000+ samples across TDS parsing methods) indicates queries are returning far too many rows or columns — likely missing pagination, projection, or filtering."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.TryReadColumnInternal","inclusivePct":1.2,"exclusivePct":0.64,"callChain":["MoveNextAsync","TryRun","TryReadColumnInternal","TryReadSqlValue","UnicodeEncoding.GetChars"],"observation":"Highest exclusive-sample application method in the SQL stack. Heavy column reading combined with UnicodeEncoding (2,546 samples for GetCharCount+GetChars) indicates many wide string columns being read — use SELECT projection to fetch only needed columns."},{"method":"Microsoft.Data.SqlClient.TdsParser.TryRun","inclusivePct":3.4,"exclusivePct":0.33,"callChain":["ExecuteReaderAsync","TryRun","TryGetTokenLength","TryProcessColumnHeaderNoNBC","TryReadSqlValue"],"observation":"Central TDS protocol dispatch loop. High inclusive time with 9+ child methods visible in the top-100 stacks confirms the application is spending significant CPU parsing large SQL result sets — classic symptom of over-fetching (SELECT * or missing WHERE/TOP clauses)."},{"method":"System.Text.Json.Serialization.Converters.ObjectDefaultConverter.OnTryWrite","inclusivePct":2.0,"exclusivePct":0.41,"callChain":["JsonSerializer.SerializeAsync","ObjectDefaultConverter.OnTryWrite","JsonPropertyInfo.GetMemberAndWriteJson","Utf8JsonWriter.WritePropertyNameSection"],"observation":"JSON serialization consumes ~2% inclusive CPU across 12 related methods. Large response payloads with many properties (WriteNullSection: 319 samples suggests many null fields being serialized). Consider using [JsonIgnore] for null properties or trimming response DTOs."},{"method":"Microsoft.Extensions.DependencyInjection.ServiceProvider.GetService","inclusivePct":1.0,"exclusivePct":0.15,"callChain":["Controller Activation","ServiceProvider.GetService","ResolveService","Dictionary.FindValue(ServiceCacheKey)"],"observation":"DI resolution accounts for ~2,300 samples including dictionary lookups. The ServiceCacheKey dictionary operations (776 FindValue + 269 TryInsert) suggest many transient services being resolved per request. Consider promoting frequently-used services to scoped or singleton lifetime."},{"method":"Microsoft.AspNetCore.Mvc.ViewFeatures.Buffers.ViewBuffer.WriteToAsync","inclusivePct":0.25,"exclusivePct":0.15,"callChain":["MvcPipeline","ViewResultExecutor","ViewBuffer.WriteToAsync","StreamWriter.Flush"],"observation":"View rendering appearing in an API workload is unexpected — this suggests the API is returning rendered views (Razor) instead of pure JSON for some endpoints. If these are API endpoints, switch to returning ObjectResult/JsonResult instead of ViewResult."},{"method":"System.Threading.SemaphoreSlim.Wait","inclusivePct":0.15,"exclusivePct":0.15,"callChain":["AsyncPipeline","SemaphoreSlim.Wait"],"observation":"Synchronous SemaphoreSlim.Wait (not WaitAsync) blocks a thread under load. Combined with SpinWait.SpinOnceCore (307 samples), this indicates thread pool contention from synchronous blocking in an async pipeline. Convert to WaitAsync to avoid thread starvation."},{"method":"System.Runtime.CompilerServices.CastHelpers.IsInstanceOfInterface","inclusivePct":1.05,"exclusivePct":1.05,"callChain":["DI/EF Core/JSON pipeline","CastHelpers.IsInstanceOfInterface"],"observation":"Type casting methods collectively consume 3.6% of CPU (8,500+ samples across 8 CastHelpers variants). This is driven by heavy polymorphic dispatch in DI resolution, EF Core materialization, and JSON serialization — reducing object counts via projection and DTO trimming would lower this."},{"method":"Microsoft.Extensions.Logging.Console.AnsiParser.Parse","inclusivePct":0.09,"exclusivePct":0.09,"callChain":["Logger.Log","ConsoleLogger.WriteMessage","AnsiParser.Parse","StringWriter.Write"],"observation":"Console logging with ANSI parsing active under load. Combined with Logger.IsEnabled checks (510 samples), logging adds measurable overhead. Set minimum log level to Warning in production or switch to a structured async logger to reduce per-request cost."},{"method":"System.Threading.ExecutionContext.SetLocalValue","inclusivePct":0.45,"exclusivePct":0.19,"callChain":["AsyncStateMachine","ExecutionContext.RunInternal","SetLocalValue","OnValuesChanged"],"observation":"Async execution context management (1,400+ samples across 3 methods) reflects the high number of async state machine transitions per request. Reducing the depth of async call chains or batching I/O operations would decrease this overhead."}],"summary":"CPU time is dominated by SQL data reading and parsing (~5.5% inclusive under ToListAsync), indicating the application is over-fetching data — likely loading full entities with all columns when only a subset is needed. The top optimization targets are: (1) Add projection (Select) and pagination to EF Core queries to drastically reduce rows and columns read; (2) Trim JSON response DTOs to avoid serializing unnecessary/null properties (~2% CPU); (3) Investigate the unexpected Razor view rendering in the API pipeline; (4) Replace synchronous SemaphoreSlim.Wait with WaitAsync to prevent thread pool starvation under load. The heavy type-casting overhead (3.6%) is a secondary indicator that will improve naturally as object counts decrease through better query projection."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.71,"gen1Rate":0.59,"gen2Rate":0.03,"pauseTimeMs":{"avg":5.5,"max":45.5,"total":886.2},"gcPauseRatio":0.7,"fragmentationPct":0.0,"observations":["Gen1:Gen0 ratio is 83.5% (71/85) — nearly every Gen0 collection promotes survivors to Gen1, indicating objects consistently outlive Gen0 but not Gen1. This pattern suggests mid-lived allocations (e.g., request-scoped objects, buffers, or EF-tracked entities) that survive initial collection. Consider object pooling or reducing per-request allocation lifetimes.","Gen0 avg pause of 6.0ms and max of 45.5ms are elevated for a workload GC. The 45.5ms max pause directly contributes to p95 tail latency. This suggests large Gen0 nursery sizes or expensive object graph scanning during promotion.","Gen2 collections are minimal (4 total, 2.5ms avg pause) — long-lived objects and LOH behavior are healthy. The application is not leaking long-lived references.","GC pause ratio of 0.7% is well within healthy bounds (<5%), so GC is not the primary latency bottleneck, but the max pause spikes (45.5ms) still contribute to p95 outliers.","Total allocation volume of ~33.8 GB over the load test (~281 MB/sec) is very high. This massive allocation throughput drives the frequent Gen0/Gen1 collection cycles and is the root cause of GC pressure."]},"heapAnalysis":{"peakSizeMB":842.24,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak managed heap of 842 MB under load is substantial for an API workload. Combined with 33.8 GB total allocation, this indicates a high object churn rate — the heap fills and collects rapidly rather than growing monotonically.","Zero fragmentation indicates the LOH is not problematic and pinned objects are not causing memory holes. The GC is compacting efficiently.","The ratio of total allocations (33,783 MB) to peak heap (842 MB) yields ~40x churn — objects are being allocated and collected aggressively, confirming short-to-mid-lived allocation dominance."]},"topAllocators":[{"type":"(allocation tick data not captured)","allocMB":null,"pctOfTotal":null,"callSite":"unknown — allocation sampling was not available in this trace","observation":"Allocation tick events were not captured or the alloc rate/top types were not exported. To identify the specific types driving the 281 MB/sec allocation rate, re-collect with PerfView using /DotNetAllocSampled and export via the 'GC Heap Alloc Stacks' view. Given EF Core + Web API patterns, likely culprits are: System.String (JSON serialization, query building), System.Byte[] (response buffering, Kestrel I/O), EF ChangeTracker entry objects, and LINQ intermediate collections (closures, iterators, SelectMany buffers)."}],"summary":"The primary memory concern is an extremely high Gen1:Gen0 promotion ratio (83.5%) combined with ~281 MB/sec total allocation throughput, producing ~33.8 GB of allocations during the test. While the overall GC pause ratio (0.7%) is healthy, individual pause spikes up to 45.5ms contribute to the 537ms p95 latency tail. The #1 optimization focus should be reducing per-request allocation volume — investigate EF Core query materialization (use AsNoTracking, project to DTOs instead of full entities), buffer pooling with ArrayPool<byte>, and caching repeated query results. Without allocation tick data the exact hotspot types are unknown; re-collecting with /DotNetAllocSampled is strongly recommended to pinpoint the top allocating call sites."}
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
