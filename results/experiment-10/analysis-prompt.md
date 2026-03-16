Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 10)
- p95 Latency: 591.942145ms
- Requests/sec: 980.9
- Error rate: 0%
- Improvement vs baseline: 92.2%

## Baseline Performance
- p95 Latency: 7546.103045ms
- Requests/sec: 125.5
- Error rate: 0%

## Runtime Counters
- CPU avg: 14.66%
- GC heap max: 1127MB
- Gen2 collections: 0
- Thread pool max threads: 41

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


## Known Optimization Queue
- [TRIED] `SampleApi/Pages/Cart/Index.cshtml.cs` — Eliminate full CartItems table scan and N+1 product lookups in Cart page LoadCart *(experiment 7 — improved)*
- [TRIED] `SampleApi/Pages/Index.cshtml.cs` — Replace dual full-table scans with targeted queries on Home page *(experiment 8 — improved)*
- [TRIED] `SampleApi/Pages/Products/Index.cshtml.cs` — Replace full product table scan with server-side filtering and pagination *(experiment 9 — improved)*

## Last Experiment's Fix
Replace full product table scan with server-side filtering and pagination

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


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":2.2,"exclusivePct":2.2,"callChain":["ToListAsync","SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun","TdsParserStateObject.TryReadChar"],"observation":"Highest exclusive-sample application method in the profile. Reading characters one at a time from the TDS network stream indicates the query returns large or numerous nvarchar/string columns — consider projecting only needed columns via Select()."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable.MoveNextAsync","inclusivePct":12.5,"exclusivePct":0.3,"callChain":["Controller Action","ToListAsync","SingleQueryingEnumerable.MoveNextAsync"],"observation":"EF Core row materialization loop; high inclusive % covers all SQL reading, TDS parsing, Unicode decoding, and object creation beneath it. The volume of SQL-related samples suggests the query fetches many rows — add pagination (Skip/Take) or server-side filtering to reduce row count."},{"method":"System.Text.Json.Serialization.Converters.StringConverter.Write","inclusivePct":1.1,"exclusivePct":1.1,"callChain":["Controller Action","JsonSerializer.SerializeAsync","ObjectDefaultConverter.OnTryWrite","JsonPropertyInfo.GetMemberAndWriteJson","StringConverter.Write"],"observation":"JSON string serialization is the top serialization hotspot. Combined with ObjectDefaultConverter.OnTryWrite (941), JsonPropertyInfo writes (453), and Utf8JsonWriter methods (~650+ samples), JSON output accounts for ~3% of CPU. Large response payloads with many string properties are being serialized — use projection to return fewer fields."},{"method":"System.Runtime.CompilerServices.CastHelpers.IsInstanceOfInterface","inclusivePct":1.1,"exclusivePct":1.1,"callChain":["EF Core Materialization","CastHelpers.IsInstanceOfInterface"],"observation":"Interface type checks (3030 samples) plus IsInstanceOfClass (2679), ChkCastAny (887), and StelemRef (1027+513) total ~8000+ samples (~3%). This reflects heavy polymorphic dispatch during EF Core entity materialization and DI resolution — AsNoTracking() can reduce change-tracker casting overhead."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.TryReadColumnInternal","inclusivePct":5.8,"exclusivePct":0.6,"callChain":["MoveNextAsync","SqlDataReader.ReadAsync","TryReadInternal","TryReadColumnInternal"],"observation":"Column-by-column reading loop with high inclusive cost covering TDS parsing, Unicode decoding, and string construction. The number of columns being read per row is a key multiplier — use Select() projection to limit columns fetched from the database."},{"method":"System.Text.UnicodeEncoding.GetCharCount + GetChars","inclusivePct":0.9,"exclusivePct":0.9,"callChain":["TdsParser.TryReadSqlStringValue","TdsParser.TryReadPlpUnicodeCharsChunk","UnicodeEncoding.GetCharCount/GetChars"],"observation":"Unicode string decoding (1615+1059 = 2674 samples) is driven by nvarchar column reads. Combined with String.CreateStringFromEncoding (355) and Buffer.Memmove (1304), string materialization from SQL results is a significant cost — fetching fewer or shorter string columns would reduce this."},{"method":"Microsoft.Extensions.DependencyInjection (ResolveService + ServiceProvider.GetService)","inclusivePct":1.2,"exclusivePct":0.4,"callChain":["Kestrel Request Pipeline","ServiceProvider.GetService","dynamicClass.ResolveService"],"observation":"DI resolution (894+292+718+241 = ~2145 samples) suggests frequent per-request service resolution. If scoped services are resolved multiple times per request, consider injecting them once in the constructor rather than resolving via IServiceProvider repeatedly."},{"method":"System.Threading.ExecutionContext.Capture + OnValuesChanged","inclusivePct":1.0,"exclusivePct":0.5,"callChain":["AsyncMethodBuilderCore.Start","ExecutionContext.Capture","ExecutionContext.OnValuesChanged"],"observation":"Async state machine overhead (Capture: 667, OnValuesChanged: 693, AsyncMethodBuilderCore.Start: 812, ScheduleAndStart: 332) totals ~2500+ samples. This is proportional to the number of async calls per request — reducing unnecessary async layers or batching async operations would help."},{"method":"System.Threading.SemaphoreSlim.Wait","inclusivePct":0.13,"exclusivePct":0.13,"callChain":["Request Pipeline","SemaphoreSlim.Wait"],"observation":"Synchronous semaphore wait (376 samples) in an async web application causes thread pool starvation under load. This likely originates from a synchronous-over-async pattern or connection pool throttling — should be converted to WaitAsync()."},{"method":"System.DefaultBinder.SelectMethod + RuntimeType.GetMethodImplCommon","inclusivePct":0.6,"exclusivePct":0.4,"callChain":["EF Core / JSON Serialization Metadata","RuntimeType.GetMethodImplCommon","DefaultBinder.SelectMethod"],"observation":"Reflection-based method lookup (620+454+404+232 = ~1710 samples) indicates runtime metadata resolution that should ideally be cached. This may stem from EF Core model building or JSON serializer metadata — ensure DbContext model is cached and JsonSerializerOptions are reused."}],"summary":"The CPU profile is dominated by SQL data reading (~30% inclusive from EF Core through SqlClient TDS parsing, Unicode decoding, and string materialization), indicating the API fetches too many rows and/or columns per request. JSON serialization adds another ~3% writing large string-heavy response payloads. The most actionable optimizations are: (1) add pagination or server-side filtering to reduce row count, (2) use Select() projection to fetch only needed columns instead of full entities, (3) add AsNoTracking() to eliminate change-tracker overhead, and (4) investigate the synchronous SemaphoreSlim.Wait that risks thread pool starvation under load."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.89,"gen1Rate":0.68,"gen2Rate":0.03,"pauseTimeMs":{"avg":8.4,"max":155.7,"total":1627.3},"gcPauseRatio":1.4,"fragmentationPct":0.0,"observations":["Gen1 collection count (82) is unusually close to Gen0 (107) — a Gen1/Gen0 ratio of 0.77 indicates most short-lived objects survive Gen0 and get promoted, suggesting allocations are mid-sized or held just long enough to escape ephemeral collection","Max GC pause of 155.7ms (Gen0) directly contributes to the 591ms p95 latency — a single blocking GC pause of this magnitude can stall in-flight requests and spike tail latency","Gen1 max pause of 114.4ms is also severe — both ephemeral generations are producing pauses well above the 50ms danger threshold for latency-sensitive APIs","Gen2 collections are rare (4 total, 2.1ms avg pause) — long-lived object promotion and LOH pressure are not a concern; the problem is entirely in ephemeral GC","GC pause ratio of 1.4% is below the 5% alarm threshold but the max-pause outliers are the real problem — average-based metrics mask the tail-latency impact"]},"heapAnalysis":{"peakSizeMB":1084.92,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 1084MB under load is extremely high for an API service — this suggests large object graphs being materialized per-request or significant caching without bounds","Total allocation volume of 47,038MB over the test (~392 MB/sec) is massive — this allocation rate drives the high GC frequency and the long pause times as the collector must scan large ephemeral segments","Zero fragmentation is good — LOH compaction is not needed, confirming the issue is allocation volume in the small object heap rather than large object layout"]},"topAllocators":[{"type":"(allocation type data unavailable)","allocMB":null,"pctOfTotal":null,"callSite":"Unknown — allocation tick sampling was not captured or top types were empty","observation":"Re-run PerfView with /DotNetAllocSampled and export GC Heap Alloc Stacks to identify which types and call sites are responsible for the ~392 MB/sec allocation rate. Without this data, focus on Entity Framework query materialization and serialization as the most likely sources given the API workload pattern."}],"summary":"The API is allocating ~392 MB/sec (47GB total), driving 189 Gen0+Gen1 collections with max pauses of 155.7ms that directly inflate the 591ms p95 latency. The unusually high Gen1/Gen0 ratio (0.77) suggests objects are living just long enough to survive Gen0 — likely EF Core materialized entities or serialization buffers held across async awaits. The #1 fix is to reduce per-request allocation volume: investigate unbounded or over-fetching EF queries (use projection with .Select() instead of full entity materialization), enable response compression, pool serialization buffers with ArrayPool<T> or RecyclableMemoryStream, and consider Server GC mode if not already enabled to reduce pause durations on multi-core hardware."}
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
