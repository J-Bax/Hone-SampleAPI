Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 15)
- p95 Latency: 548.763115ms
- Requests/sec: 1032.4
- Error rate: 0%
- Improvement vs baseline: 92.7%

## Baseline Performance
- p95 Latency: 7546.103045ms
- Requests/sec: 125.5
- Error rate: 0%

## Runtime Counters
- CPU avg: 15.16%
- GC heap max: 1088MB
- Gen2 collections: 26907304
- Thread pool max threads: 36

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


## Known Optimization Queue
- [TRIED] `SampleApi/Controllers/ReviewsController.cs` — Consolidate redundant DB round trips in review endpoints *(experiment 12 — improved)*
- [TRIED] `SampleApi/Controllers/CartController.cs` — Add AsNoTracking to read-only cart and product queries *(experiment 13 — improved)*
- [TRIED] `SampleApi/Controllers/OrdersController.cs` — Replace full table scans and N+1 queries in order read endpoints *(experiment 14 — stale)*

## Last Experiment's Fix
Replace full table scans and N+1 queries in order read endpoints

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


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":6.6,"exclusivePct":6.6,"callChain":["ToListAsync","SingleQueryingEnumerable.MoveNextAsync","TdsParser.TryRun","TryReadColumnInternal","TryReadChar"],"observation":"Highest exclusive-time application frame — character-by-character TDS parsing indicates large volumes of string/text data being read from SQL Server, suggesting queries return excessive column data or too many rows."},{"method":"Microsoft.Data.SqlClient (aggregate TDS parsing)","inclusivePct":19.5,"exclusivePct":19.5,"callChain":["ToListAsync","ExecuteReaderAsync","TdsParser.TryRun","TryReadColumnInternal","TryReadSqlStringValue","TryReadPlpUnicodeCharsChunk"],"observation":"SqlClient TDS parsing collectively dominates CPU (~19.5%). Methods like TryReadPlpUnicodeCharsChunk, TryReadSqlStringValue, and StateSnapshot.Snap/PushBuffer indicate reading many large string columns. SELECT N+1 or SELECT * pattern likely — use projection to fetch only needed columns."},{"method":"System.Text.Json.Serialization.Converters.StringConverter.Write","inclusivePct":3.0,"exclusivePct":3.0,"callChain":["Controller.Action","JsonSerializer.SerializeAsync","ObjectDefaultConverter.OnTryWrite","GetMemberAndWriteJson","StringConverter.Write"],"observation":"JSON string serialization alone is 3% of CPU. Combined with other System.Text.Json frames (~7.2% total), response serialization is a significant cost — likely serializing large collections with many string properties. Use source generators or reduce payload size."},{"method":"System.Runtime.CompilerServices.CastHelpers (aggregate type checks)","inclusivePct":9.5,"exclusivePct":9.5,"callChain":["Various call sites","IsInstanceOfInterface","IsInstanceOfClass","ChkCastAny"],"observation":"Type-checking operations consume ~9.5% of CPU — IsInstanceOfInterface (3.0%), IsInstanceOfClass (2.7%), and others. This indicates heavy polymorphic dispatch, likely from EF Core materializing entities through interface-heavy patterns and DI resolution. Not directly actionable but correlates with object-heavy data paths."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable.MoveNextAsync","inclusivePct":0.9,"exclusivePct":0.9,"callChain":["ToListAsync","MoveNextAsync","TdsParser.TryRun","TryReadColumnInternal"],"observation":"EF Core query enumeration is the gateway to all SQL reading. Low exclusive % but high inclusive cost — the real CPU is in TDS parsing beneath it. Optimize the LINQ queries to use .Select() projections and avoid materializing full entities."},{"method":"System.Text.UnicodeEncoding.GetCharCount + GetChars","inclusivePct":2.8,"exclusivePct":2.8,"callChain":["TdsParser.TryReadSqlStringValue","TryReadPlpUnicodeCharsChunk","UnicodeEncoding.GetCharCount","UnicodeEncoding.GetChars"],"observation":"Unicode string decoding at 2.8% is driven by SQL string column reads. Large nvarchar/ntext columns are being decoded character-by-character. Use column projection to avoid reading unnecessary text fields."},{"method":"Microsoft.Extensions.DependencyInjection.ServiceProvider.GetService + ResolveService","inclusivePct":2.2,"exclusivePct":2.2,"callChain":["HttpProtocol.ProcessRequests","ControllerFactory","ServiceProvider.GetService","ResolveService","Dictionary.FindValue(ServiceCacheKey)"],"observation":"DI resolution at 2.2% with ServiceCacheKey dictionary lookups suggests per-request service resolution overhead. Review service lifetimes — promote frequently-resolved transient services to scoped or singleton where safe."},{"method":"System.Reflection.DefaultBinder.SelectMethod + RuntimeType.GetMethodImplCommon","inclusivePct":1.4,"exclusivePct":1.4,"callChain":["MVC ActionInvoker","RuntimeType.GetMethodImplCommon","DefaultBinder.SelectMethod"],"observation":"Reflection-based method resolution at 1.4% indicates runtime type inspection per request — possibly from model binding or MVC action selection. Consider caching or compiled expressions for hot paths."},{"method":"Microsoft.AspNetCore.Mvc.ViewFeatures.Buffers.ViewBuffer.WriteToAsync","inclusivePct":0.35,"exclusivePct":0.35,"callChain":["MVC Pipeline","ViewResultExecutor","ViewBuffer.WriteToAsync"],"observation":"ViewBuffer usage in an API suggests Razor view rendering for responses instead of pure JSON serialization. If this is an API, switching to ObjectResult/JSON-only responses eliminates view rendering overhead entirely."},{"method":"System.Threading.SemaphoreSlim.Wait","inclusivePct":0.36,"exclusivePct":0.36,"callChain":["Async pipeline","SemaphoreSlim.Wait"],"observation":"Synchronous SemaphoreSlim.Wait (not WaitAsync) blocks a thread pool thread. Under load this causes thread pool starvation and latency spikes. Convert to await SemaphoreSlim.WaitAsync() or identify the synchronous caller."}],"summary":"The CPU profile is overwhelmingly dominated by SQL data reading (~19.5% in TDS parsing) and JSON response serialization (~7.2%), indicating the API fetches too much data from the database and serializes large payloads. The most actionable optimizations are: (1) Add .Select() projections to EF Core queries to fetch only required columns — this will reduce TDS parsing, Unicode decoding, and object materialization costs simultaneously; (2) Reduce response payload size or use System.Text.Json source generators; (3) Investigate the synchronous SemaphoreSlim.Wait which risks thread pool starvation under load. The DI resolution overhead (2.2%) and Razor ViewBuffer usage also suggest architectural improvements — ensure API endpoints return JSON directly, not through Razor views."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.87,"gen1Rate":0.74,"gen2Rate":0.03,"pauseTimeMs":{"avg":5.76,"max":63.7,"total":1134.8},"gcPauseRatio":1.0,"fragmentationPct":0.0,"observations":["Gen1 count (89) is abnormally close to Gen0 count (104) — 86% of Gen0 collections escalate to Gen1, indicating a large volume of objects survive Gen0 but die shortly after. This mid-lived allocation pattern (e.g., request-scoped buffers, EF tracking objects, LINQ materializations) is the dominant memory behavior.","Gen2 collections are very low (4 total) — long-lived object promotion is well-controlled and LOH pressure is minimal.","GC pause ratio of 1.0% is healthy (well under the 5% concern threshold), so GC is not a primary throughput bottleneck.","Max GC pause of 63.7ms exceeds the 50ms threshold and directly contributes to p95 latency (548ms). This single Gen0 pause spike likely coincided with a Gen1 escalation under heavy allocation pressure.","Total allocations of ~49 GB over the load test (~408 MB/sec allocation rate) represent significant allocation volume for ~1032 req/sec — roughly 395 KB allocated per request, suggesting materializing large result sets or repeated serialization buffers."]},"heapAnalysis":{"peakSizeMB":1118.73,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 1118 MB is substantial for an API workload at 1032 req/sec — this suggests large object graphs are alive simultaneously, likely from EF change tracking holding materialized entities across concurrent requests.","Zero fragmentation indicates the LOH is not problematic and the GC is compacting effectively.","With 49 GB total allocated but only ~1.1 GB peak live, objects are short-to-mid-lived — the GC is reclaiming aggressively, but the sheer allocation volume forces frequent collections."]},"topAllocators":[{"type":"(allocation type breakdown unavailable)","allocMB":null,"pctOfTotal":null,"callSite":"Unknown — allocation tick data was not captured or export yielded no type-level breakdown","observation":"Re-run PerfView with /DotNetAllocSampled and export via 'GC Heap Alloc Stacks' view to identify the specific types driving the ~408 MB/sec allocation rate. Without this data, optimization must target patterns inferred from GC behavior: EF query materialization, JSON serialization buffers, and LINQ intermediate collections are the most likely culprits given the Gen0→Gen1 escalation pattern."}],"summary":"The API allocates ~49 GB during the load test (~395 KB/request) with an unusual Gen0-to-Gen1 escalation rate of 86%, pointing to mid-lived objects — most likely EF Core change-tracked entities and serialization buffers that outlive Gen0's budget. The #1 fix is to use AsNoTracking() on read-only EF queries and consider response caching or object pooling for repeated allocations, which will reduce both the Gen1 collection frequency and the 63.7ms max pause spikes contributing to the 548ms p95 latency."}
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
