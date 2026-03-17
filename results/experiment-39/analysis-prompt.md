Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 39)
- p95 Latency: 513.94764ms
- Requests/sec: 1166.3
- Error rate: 0%
- Improvement vs baseline: 93.2%

## Baseline Performance
- p95 Latency: 7546.103045ms
- Requests/sec: 125.5
- Error rate: 0%

## Runtime Counters
- CPU avg: 13.44%
- GC heap max: 817MB
- Gen2 collections: 0
- Thread pool max threads: 38

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
| 33 | 2026-03-16 12:17 | `SampleApi/Pages/Products/Detail.cshtml.cs` | Add Select projections to Reviews and RelatedProducts queries on Detail page | improved |
| 34 | 2026-03-16 12:41 | `SampleApi/Pages/Products/Index.cshtml.cs` | Add Select projection to paginated product query excluding Description | improved |
| 35 | 2026-03-16 13:06 | `SampleApi/Program.cs` | Set minimum log level to Warning to reduce per-request logging overhead | improved |
| 36 | 2026-03-16 20:15 | `SampleApi/Controllers/ProductsController.cs` | Test failure: Add result limit to search endpoint returning all 1000 products | regressed |
| 37 | 2026-03-16 20:39 | `SampleApi/Pages/Index.cshtml.cs` | Replace ORDER BY NEWID() with efficient Skip/Take random sampling | improved |
| 38 | 2026-03-16 21:04 | `SampleApi/Pages/Checkout/Index.cshtml.cs` | Use AsNoTracking and raw SQL DELETE for cart cleanup in checkout | improved |


## Known Optimization Queue
- [TRIED] `SampleApi/Controllers/ProductsController.cs` — Add result limit to search endpoint returning all 1000 products *(experiment 36 — regressed)*
- [TRIED] `SampleApi/Pages/Index.cshtml.cs` — Replace ORDER BY NEWID() with efficient Skip/Take random sampling *(experiment 37 — improved)*
- [TRIED] `SampleApi/Pages/Checkout/Index.cshtml.cs` — Use AsNoTracking and raw SQL DELETE for cart cleanup in checkout *(experiment 38 — improved)*

## Last Experiment's Fix
Use AsNoTracking and raw SQL DELETE for cart cleanup in checkout

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
| 33 | — | improved | 543.8 | 1140.3 | hone/experiment-33 |
| 34 | — | improved | 535.4 | 1122.1 | hone/experiment-34 |
| 35 | — | improved | 544.4 | 1129.2 | hone/experiment-35 |
| 36 | — | test_failure | N/A | N/A | hone/experiment-36 |
| 37 | — | improved | 517.3 | 1155.3 | hone/experiment-37 |
| 38 | — | improved | 513.9 | 1166.3 | hone/experiment-38 |


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"sqlmin/sqllang/sqldk/sqltses (SQL Server Engine)","inclusivePct":12.7,"exclusivePct":12.7,"callChain":["EF Core Query","SqlClient","TDS Protocol","SQL Server Engine (sqlmin/sqllang/sqldk/sqltses)"],"observation":"SQL Server engine consumes ~60K samples (12.7%) — the single largest application-related cost. Combined with SQL client-side parsing (~1%), database work dominates the profile. This strongly suggests queries are doing excessive work: returning too many rows, missing indexes, or N+1 query patterns."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.TryReadColumnInternal","inclusivePct":1.03,"exclusivePct":0.23,"callChain":["EF Core SingleQueryingEnumerable.MoveNextAsync","RelationalCommand.ExecuteReaderAsync","SqlDataReader.ReadAsync","SqlDataReader.TryReadColumnInternal"],"observation":"Highest exclusive-sample SQL client method (1,072 samples). The broad spread across TryReadColumnInternal, TryGetTokenLength, TryProcessColumnHeaderNoNBC, and TryReadByte indicates the app is reading many columns per row or many rows per query. Consider using projection (Select) to limit columns returned."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1+AsyncEnumerator.MoveNextAsync","inclusivePct":0.16,"exclusivePct":0.11,"callChain":["Controller Action","EF Core DbSet Query","SingleQueryingEnumerable.MoveNextAsync"],"observation":"EF Core's query enumeration shows up as a leaf frame (518 samples), meaning time is spent in the enumerator's own logic — materializing entities, tracking changes, and fixing up navigation properties. Consider using AsNoTracking() for read-only queries or switching to raw SQL/Dapper for hot paths."},{"method":"System.Text.Json.Serialization.Converters.ObjectDefaultConverter`1.OnTryWrite","inclusivePct":0.51,"exclusivePct":0.14,"callChain":["Controller Action","JsonResult/OutputFormatter","JsonSerializer.Serialize","ObjectDefaultConverter.OnTryWrite"],"observation":"JSON serialization collectively accounts for ~2,400 samples (0.51%). The ObjectDefaultConverter is the generic reflection-based path. The presence of WriteNullSection (245 samples) suggests many null properties being serialized — consider [JsonIgnore(Condition = WhenWritingNull)] or a source generator for System.Text.Json to eliminate reflection overhead."},{"method":"Microsoft.Extensions.DependencyInjection.ServiceProvider.GetService + ResolveService","inclusivePct":0.3,"exclusivePct":0.18,"callChain":["Request Pipeline","ServiceProviderEngineScope","ResolveService/GetService","Dictionary.FindValue(ServiceCacheKey)"],"observation":"DI resolution appears with 1,402 combined samples including dictionary lookups on ServiceCacheKey. This suggests either many transient service resolutions per request or a deep dependency graph. Consider reducing the number of injected services in hot-path controllers or switching frequently-resolved transients to singletons where safe."},{"method":"System.Runtime.CompilerServices.CastHelpers (IsInstanceOfInterface/IsInstanceOfClass/ChkCast)","inclusivePct":1.15,"exclusivePct":1.15,"callChain":["Various call sites","CastHelpers.IsInstanceOfInterface/IsInstanceOfClass/ChkCastAny/StelemRef"],"observation":"Type-casting checks consume ~5,420 samples (1.15%) — more than the entire SQL client stack. IsInstanceOfInterface (1,740) and IsInstanceOfClass (1,446) dominate. This indicates heavy polymorphic dispatch, likely from EF Core's internal materialization and DI resolution. While not directly fixable, reducing entity complexity and interface-heavy abstractions on hot paths would help."},{"method":"System.Text.UnicodeEncoding.GetCharCount + GetChars","inclusivePct":0.37,"exclusivePct":0.37,"callChain":["SqlDataReader.TryReadColumnInternal","TdsParser.TryReadSqlValue","UnicodeEncoding.GetCharCount/GetChars"],"observation":"Unicode string decoding (1,739 combined samples) is driven by SQL client reading NVARCHAR/NCHAR columns from the TDS stream. If queries return large string columns that aren't needed, projecting only required columns would reduce this cost."},{"method":"System.Threading.SemaphoreSlim.Wait","inclusivePct":0.05,"exclusivePct":0.05,"callChain":["Async pipeline","SemaphoreSlim.Wait"],"observation":"Synchronous SemaphoreSlim.Wait (254 samples) in an async API indicates a sync-over-async bottleneck. This blocks a thread pool thread and hurts throughput under load. Identify the caller and switch to WaitAsync()."},{"method":"System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start + ExecutionContext overhead","inclusivePct":0.38,"exclusivePct":0.38,"callChain":["Any async method","AsyncMethodBuilderCore.Start","ExecutionContext.OnValuesChanged/SetLocalValue/RunInternal"],"observation":"Async state machine startup and ExecutionContext flow account for ~1,786 samples. Deep async call chains amplify this cost. Consider reducing async nesting depth on the hot request path or batching work to reduce the number of async transitions."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.WillHaveEnoughData","inclusivePct":0.12,"exclusivePct":0.12,"callChain":["SqlDataReader.TryReadColumnInternal","SqlDataReader.WillHaveEnoughData"],"observation":"This method (547 samples) checks whether the TDS buffer has enough data before reading each column. Its prominence suggests many small column reads per row — a wide table or SELECT * pattern. Use explicit column projection to reduce per-row overhead."}],"summary":"The CPU profile is dominated by database work: SQL Server engine processing (12.7%) plus .NET SQL client parsing (1%) make data access the clear optimization priority. The high sample counts in TDS column-reading methods (TryReadColumnInternal, WillHaveEnoughData, TryGetTokenLength) suggest queries return too many columns or rows — apply EF Core projections (.Select()), add AsNoTracking() for read-only paths, and verify indexes cover the hot queries. Secondary targets are JSON serialization (0.5% — consider source generators and null-property suppression) and DI resolution overhead (0.3%). A synchronous SemaphoreSlim.Wait call should be converted to WaitAsync() to avoid thread pool starvation under the current 1,166 RPS load."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.75,"gen1Rate":0.59,"gen2Rate":0.04,"pauseTimeMs":{"avg":5.8,"max":45.0,"total":919.4},"gcPauseRatio":0.8,"fragmentationPct":0.0,"observations":["Gen1 collection count (68) is unusually close to Gen0 (86), indicating a ~79% promotion rate from Gen0 to Gen1 — most short-lived objects are surviving Gen0 and being promoted, suggesting mid-lifetime allocations (e.g., request-scoped objects, EF tracking structures, or LINQ materializations that outlive ephemeral scope)","Gen2 collections are very low (5) with sub-4ms pauses — long-lived object management is healthy and LOH pressure is minimal","GC pause ratio of 0.8% is well within healthy range (<5%), so GC pauses are NOT the primary driver of the 514ms p95 latency — the bottleneck lies elsewhere (likely query execution, I/O, or thread pool starvation)","Max pause of 45ms (Gen1) is notable but not extreme — however, if these pauses coincide with in-flight requests they could contribute to tail latency spikes","Total allocation throughput is approximately 293 MB/sec (33.6 GB over ~115s), which is very high and forces frequent GC cycles even though individual pauses are short"]},"heapAnalysis":{"peakSizeMB":839.36,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 839 MB under load is substantial — with 33.6 GB total allocated and only 839 MB peak, most allocations are short-lived but the sheer volume keeps the heap inflated","Zero fragmentation is a positive sign — no LOH fragmentation or pinning issues detected","The high peak-to-steady ratio suggests large transient object graphs are being materialized per-request (likely EF Core query result sets or serialization buffers)"]},"topAllocators":[{"type":"(allocation type data unavailable)","allocMB":null,"pctOfTotal":null,"callSite":"Unknown — PerfView allocation tick data was not captured or export produced no type breakdown","observation":"Re-run PerfView with /DotNetAllocSampled and export via 'GC Heap Alloc Stacks' view to identify which types and call sites are responsible for the 293 MB/sec allocation rate. Without this data, focus on common .NET API allocation hotspots: EF Core query materialization (ToListAsync), large string concatenation, JSON serialization buffers, and LINQ intermediate collections."}],"summary":"GC is NOT the p95 latency bottleneck (0.8% pause ratio), but the allocation rate of ~293 MB/sec driving 33.6 GB total allocations is extremely high and creates unnecessary GC pressure. The abnormally high Gen0-to-Gen1 promotion rate (79%) points to request-scoped objects surviving just long enough to escape Gen0 — likely EF Core change-tracking entities, LINQ materializations (ToList), or response serialization buffers. The #1 action is to reduce per-request allocation volume: use AsNoTracking() for read-only EF queries, avoid materializing full collections when streaming or pagination would suffice, and consider object pooling (ArrayPool<T>, ObjectPool<T>) for hot-path buffers. To pinpoint exact types, re-collect with allocation sampling enabled."}
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
