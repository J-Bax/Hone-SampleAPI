Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 20)
- p95 Latency: 390.930625ms
- Requests/sec: 1759.2
- Error rate: 11.11%
- Improvement vs baseline: 81%

## Baseline Performance
- p95 Latency: 2054.749925ms
- Requests/sec: 427.3
- Error rate: 11.11%

## Runtime Counters
- CPU avg: 15.01%
- GC heap max: 773MB
- Gen2 collections: 28863112
- Thread pool max threads: 34

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
| 11 | 2026-03-14 17:11 | `SampleApi/Pages/Products/Index.cshtml.cs` | Products page tracks 1000 entities needlessly on read-only render | improved |
| 12 | 2026-03-14 17:35 | `SampleApi/Controllers/OrdersController.cs` | OrdersController has N+1 queries and full table scans across multiple endpoints | improved |
| 13 | 2026-03-14 18:00 | `SampleApi/Pages/Products/Detail.cshtml.cs` | Product detail page uses tracked queries for read-only rendering | improved |
| 14 | 2026-03-14 19:13 | `SampleApi/Controllers/ProductsController.cs` | GetProducts re-queries all 1000 products from the database on every request | improved |
| 15 | 2026-03-14 19:54 | `SampleApi/Controllers/ProductsController.cs` | SearchProducts and GetProductsByCategory bypass existing product cache | improved |
| 16 | 2026-03-14 20:19 | `SampleApi/Controllers/ReviewsController.cs` | GetAverageRating makes 3 separate database round-trips per request | improved |
| 17 | 2026-03-14 20:44 | `SampleApi/Pages/Checkout/Index.cshtml.cs` | Checkout OnPostAsync uses 3 SaveChangesAsync calls where 2 suffice | improved |
| 18 | 2026-03-14 21:03 | `SampleApi/Pages/Products/Index.cshtml.cs` | Cache product list to eliminate per-request full-table DB load | queued |
| 19 | 2026-03-14 21:28 | `SampleApi/Pages/Index.cshtml.cs` | Cache featured products to avoid ORDER BY NEWID() on every request | improved |


## Known Optimization Queue
- [TRIED] `SampleApi/Pages/Products/Index.cshtml.cs` — Cache product list to eliminate per-request full-table DB load *(experiment 18 — skipped)*
- [TRIED] `SampleApi/Pages/Index.cshtml.cs` — Cache featured products to avoid ORDER BY NEWID() on every request *(experiment 19 — improved)*
- [PENDING] [ARCHITECTURE] `SampleApi/Data/AppDbContext.cs` — Add database indexes on frequently filtered foreign-key columns

## Last Experiment's Fix
Cache featured products to avoid ORDER BY NEWID() on every request

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
| 11 | — | improved | 486.5 | 1301.3 | hone/experiment-11 |
| 12 | — | improved | 471.7 | 1327.8 | hone/experiment-12 |
| 13 | — | improved | 474 | 1325.1 | hone/experiment-13 |
| 14 | — | improved | 453.4 | 1420.3 | hone/experiment-14 |
| 15 | — | improved | 427.8 | 1618.1 | hone/experiment-15 |
| 16 | — | improved | 405.3 | 1712.8 | hone/experiment-16 |
| 17 | — | improved | 407.7 | 1693.7 | hone/experiment-17 |
| 18 | — | skipped | N/A | N/A | hone/experiment-18 |
| 19 | — | improved | 390.9 | 1759.2 | hone/experiment-19 |


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"SampleApi.Controllers.ProductsController.<GetProductsByCategory>b__0","inclusivePct":0.13,"exclusivePct":0.13,"callChain":["ProductsController.GetProductsByCategory","Enumerable.Where","<GetProductsByCategory>b__0(Product)"],"observation":"Client-side filtering lambda on Product objects — indicates products are loaded into memory from SQL then filtered in C# rather than pushing the predicate into the SQL WHERE clause. Combined with the heavy TdsParser/SqlDataReader samples below, this is strong evidence of a 'fetch all, filter in memory' anti-pattern causing excessive data transfer and CPU waste."},{"method":"SampleApi.Controllers.ProductsController.<SearchProducts>b__0","inclusivePct":0.12,"exclusivePct":0.12,"callChain":["ProductsController.SearchProducts","Enumerable.Where","<SearchProducts>b__0(Product)"],"observation":"Same client-side filtering anti-pattern as GetProductsByCategory. The closure (DisplayClass9_0) captures search parameters and applies them in-process. This should be an IQueryable predicate translated to SQL, not an in-memory Func<Product, bool> delegate."},{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":0.73,"exclusivePct":0.73,"callChain":["SqlDataReader.ReadAsync","TdsParser.TryRun","TdsParser.TryReadSqlStringValue","TdsParserStateObject.TryReadChar"],"observation":"Highest exclusive-sample SQL method — reading individual characters from the TDS stream. The sheer volume (4456 samples) indicates massive amounts of string data being read from SQL Server, consistent with fetching entire tables of product data including large text columns (descriptions, names) that are then discarded by client-side filtering."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.TryReadColumnInternal","inclusivePct":0.21,"exclusivePct":0.21,"callChain":["SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","SqlDataReader.TryReadColumnInternal"],"observation":"Column-level data reading is expensive because every column of every row is materialized — even rows that will be discarded by the in-memory filter. Projecting only needed columns (Select) and filtering server-side would drastically reduce this cost."},{"method":"System.Text.Json.Serialization.Converters.ObjectDefaultConverter.OnTryWrite","inclusivePct":0.26,"exclusivePct":0.26,"callChain":["JsonSerializer.SerializeAsync","JsonConverter.TryWrite","ObjectDefaultConverter.OnTryWrite"],"observation":"JSON serialization of response objects is significant (1580 samples), compounded by related methods (ToUtf8: 1569, GetMemberAndWriteJson: 786, WriteStringMinimized: 624, WritePropertyNameSection: 560). The total JSON serialization footprint (~5800+ samples) suggests very large response payloads — likely returning full entity graphs instead of DTOs or paginated results."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable.MoveNextAsync","inclusivePct":0.13,"exclusivePct":0.13,"callChain":["ToListAsync","SingleQueryingEnumerable.AsyncEnumerator.MoveNextAsync"],"observation":"EF Core row iteration cost. When combined with the ToListAsync (330 samples), this confirms the query materializes a large number of rows. If the query used server-side filtering, far fewer rows would be enumerated."},{"method":"Microsoft.Extensions.DependencyInjection.ResolveService","inclusivePct":0.16,"exclusivePct":0.16,"callChain":["Middleware pipeline","DI ServiceProvider.GetService","ResolveService"],"observation":"DI resolution is measurable at 968 samples plus related Dictionary.FindValue (939 on ServiceCacheKey). This may indicate excessive transient service registrations or per-request resolution of deep dependency graphs. Consider scoped or singleton lifetimes where appropriate."},{"method":"System.Threading.SemaphoreSlim.Wait","inclusivePct":0.06,"exclusivePct":0.06,"callChain":["Controller action","SemaphoreSlim.Wait"],"observation":"Synchronous blocking (368 samples) in an async web application. This blocks a thread pool thread, reducing throughput under load and contributing to the high p95 latency (391ms). Should be replaced with SemaphoreSlim.WaitAsync or the blocking call should be investigated and removed."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.PrepareAsyncInvocation","inclusivePct":0.2,"exclusivePct":0.2,"callChain":["SqlDataReader.ReadAsync","PrepareAsyncInvocation","StateSnapshot.Snap/SetSnapshot"],"observation":"Async state snapshotting overhead (PrepareAsyncInvocation: 1199, Snap: 731, SetSnapshot: 482, Clear: 430, PushBuffer: 358 = ~3200 total) is high because it executes per-row for every row read. Reducing row count through server-side filtering would proportionally reduce this overhead."},{"method":"System.Runtime.CompilerServices.CastHelpers (aggregate)","inclusivePct":1.29,"exclusivePct":1.29,"callChain":["EF Core materialization","CastHelpers.IsInstanceOfClass/Interface/ChkCast"],"observation":"Type-checking overhead (IsInstanceOfClass: 2525, IsInstanceOfInterface: 1872, IsInstanceOfAny: 864, ChkCastAny: 819, ChkCastInterface: 461, StelemRef: 536+951 = ~8028 total) is driven by EF Core object materialization and collection population for large result sets. This is a downstream symptom of fetching too many rows."}],"summary":"The CPU profile is dominated by two clear anti-patterns: (1) GetProductsByCategory and SearchProducts fetch all rows from SQL Server and filter in-memory using LINQ-to-Objects delegates (the DisplayClass lambdas), causing massive TDS parsing overhead (~12K+ samples across SqlClient methods) and wasteful JSON serialization (~5800+ samples) of oversized responses; (2) a synchronous SemaphoreSlim.Wait call blocks thread pool threads under load. The immediate fix is converting the client-side Func<Product,bool> filters to IQueryable Expression<Func<Product,bool>> predicates so EF Core translates them to SQL WHERE clauses, adding pagination to limit result sizes, and replacing the synchronous Wait with WaitAsync. These changes would dramatically reduce data transfer, CPU usage across SqlClient/JSON/type-casting, and improve the 391ms p95 latency and 11% error rate."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":1.18,"gen1Rate":0.93,"gen2Rate":0.04,"pauseTimeMs":{"avg":4.55,"max":49.0,"total":1178.8},"gcPauseRatio":1.0,"fragmentationPct":0.0,"observations":["Gen1 collection count (112) is abnormally close to Gen0 (142) — 79% survival rate from Gen0 to Gen1 indicates many mid-lived objects that outlast Gen0 but die in Gen1. This pattern is typical of per-request allocations (e.g., large DTOs, EF change trackers, or serialization buffers) that survive one GC cycle due to request concurrency.","Gen2 collections are very low (5 total) and have minimal pause times (max 4.5ms), indicating long-lived objects are well-managed and LOH pressure is not a concern.","Max GC pause of 49ms (Gen0) is close to the 50ms danger threshold and can directly contribute to the elevated p95 latency of 391ms — under high concurrency a 49ms pause stalls all threads.","Overall GC pause ratio of 1.0% is within acceptable bounds, but the sheer volume of collections (259 total in ~120s) means the runtime is spending significant cumulative time in GC."]},"heapAnalysis":{"peakSizeMB":786.54,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 786MB under load is very high for an API workload — this suggests large object graphs are being materialized per-request, likely from EF Core query results or serialization buffers.","Total allocations of 47,397MB over the test duration imply an allocation rate of ~395 MB/sec, which is extremely aggressive and the primary driver of GC frequency.","Zero fragmentation is expected with workload GC since most pressure is in Gen0/Gen1 ephemeral segments — LOH is not a factor here."]},"topAllocators":[{"type":"(allocation type data unavailable)","allocMB":null,"pctOfTotal":null,"callSite":null,"observation":"No per-type allocation breakdown was captured. Likely causes at 395 MB/sec allocation rate: (1) EF Core materializing large result sets without pagination — use .Take()/.Skip() or AsNoTracking(), (2) JSON serialization creating intermediate strings — consider System.Text.Json with streaming, (3) String concatenation or LINQ .ToList() in hot paths — use ArrayPool<T> or return IAsyncEnumerable."}],"summary":"The API is allocating ~395 MB/sec, driving 259 GC collections in 120 seconds with an abnormal Gen0→Gen1 survival rate of 79%. This means most allocated objects live just long enough to escape Gen0 — a classic sign of per-request object graphs (EF Core tracked entities, large DTOs, serialization buffers) under concurrent load. The #1 fix is to reduce per-request allocation volume: add AsNoTracking() to read-only EF queries, paginate large result sets, and consider object pooling (ArrayPool<T>, ObjectPool<T>) for buffers. The 11.11% error rate combined with 786MB peak heap suggests the server may also be hitting memory limits under load, causing request failures — investigate whether errors correlate with Gen0 max-pause spikes or thread pool starvation during GC."}
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
