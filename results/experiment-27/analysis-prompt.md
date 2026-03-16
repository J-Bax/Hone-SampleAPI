Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 27)
- p95 Latency: 544.686865ms
- Requests/sec: 1087.3
- Error rate: 0%
- Improvement vs baseline: 92.8%

## Baseline Performance
- p95 Latency: 7546.103045ms
- Requests/sec: 125.5
- Error rate: 0%

## Runtime Counters
- CPU avg: 13.03%
- GC heap max: 1314MB
- Gen2 collections: 0
- Thread pool max threads: 53

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


## Known Optimization Queue
- [TRIED] `SampleApi/Program.cs` — Replace AddDbContext with AddDbContextPool to reduce allocation pressure *(experiment 24 — regressed)*
- [TRIED] `SampleApi/Pages/Index.cshtml.cs` — Replace NEWID() random ordering with efficient deterministic query for featured products *(experiment 25 — stale)*
- [TRIED] `SampleApi/Controllers/CartController.cs` — Add Select projection to product dictionary lookup in GetCart API endpoint *(experiment 26 — improved)*

## Last Experiment's Fix
Add Select projection to product dictionary lookup in GetCart API endpoint

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


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":1.1,"exclusivePct":1.1,"callChain":["SqlDataReader.ReadAsync","TdsParser.TryRun","TdsParser.TryReadPlpUnicodeCharsChunk","TdsParserStateObject.TryReadChar"],"observation":"Highest exclusive-sample managed method — character-by-character TDS stream reading dominates CPU. Large result sets with many string columns amplify this cost; reducing columns selected or result-set size would cut time here."},{"method":"System.Text.Json.Serialization.Converters.StringConverter.Write","inclusivePct":0.56,"exclusivePct":0.56,"callChain":["JsonSerializer.SerializeAsync","ObjectDefaultConverter.OnTryWrite","JsonPropertyInfo.GetMemberAndWriteJson","StringConverter.Write"],"observation":"JSON serialization of string properties is a significant CPU consumer. Many string fields per response object multiply this cost. Consider reducing payload size, using source-generated JSON serializers, or trimming unnecessary string properties from responses."},{"method":"Microsoft.Data.SqlClient.SqlDataReader (aggregate)","inclusivePct":3.8,"exclusivePct":0.9,"callChain":["EF.SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","SqlDataReader.TryReadColumnInternal","SqlDataReader.PrepareAsyncInvocation"],"observation":"SqlDataReader methods (TryReadColumnInternal 1807, PrepareAsyncInvocation 1470, WillHaveEnoughData 670, ReadAsync 650, CheckDataIsReady 386, TryReadInternal 376) collectively dominate. The reader is materializing many columns per row and many rows per query — classic sign of SELECT * or over-fetching patterns."},{"method":"Microsoft.Data.SqlClient.TdsParser.TryRun","inclusivePct":5.0,"exclusivePct":0.12,"callChain":["SqlDataReader.ReadAsync","TdsParser.TryRun"],"observation":"High inclusive but low exclusive % — this is the main TDS protocol dispatch loop. All SQL data reading flows through here. The cost is in the callees (column parsing, string reading, value extraction). Reducing data volume is the lever."},{"method":"Microsoft.Data.SqlClient.TdsParser.TryReadPlpUnicodeCharsChunk","inclusivePct":0.45,"exclusivePct":0.21,"callChain":["TdsParser.TryRun","TdsParser.TryReadSqlValue","TdsParser.TryReadPlpUnicodeCharsChunk"],"observation":"Unicode PLP (Partial Length Prefixed) chunk reading indicates large nvarchar(max) or ntext columns being streamed. If the API doesn't need full text content, projecting only needed columns or truncating at the SQL level would reduce this."},{"method":"Microsoft.Data.SqlClient.TdsParserStateObject+StateSnapshot (aggregate)","inclusivePct":0.35,"exclusivePct":0.35,"callChain":["TdsParser.TryRun","TdsParserStateObject.SetSnapshot","StateSnapshot.Snap/Clear/PushBuffer"],"observation":"Snapshot management (Snap 1092, Clear 602, SetSnapshot 471, PushBuffer 440) for async TDS reads is costly. This overhead scales with the number of columns and rows read asynchronously. Fewer columns/rows = fewer snapshots."},{"method":"System.Text.UnicodeEncoding.GetCharCount / GetChars","inclusivePct":0.44,"exclusivePct":0.44,"callChain":["TdsParser.TryReadSqlStringValue","UnicodeEncoding.GetCharCount","UnicodeEncoding.GetChars","String.CreateStringFromEncoding"],"observation":"Unicode decoding for SQL string values. Combined with TryReadPlpUnicodeCharsChunk, this confirms large volumes of string data being transferred from SQL Server. The API is likely returning more text data than clients need."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable.MoveNextAsync","inclusivePct":0.15,"exclusivePct":0.15,"callChain":["Controller.Action","EF.ToListAsync","SingleQueryingEnumerable.MoveNextAsync"],"observation":"EF Core row enumeration appears in the profile. While its exclusive cost is modest, it's the entry point for all the SqlDataReader/TdsParser work below. Adding .Select() projections or .AsNoTracking() at this level would reduce downstream costs."},{"method":"Microsoft.Extensions.DependencyInjection.ResolveService","inclusivePct":0.17,"exclusivePct":0.17,"callChain":["Middleware","DI.ResolveService"],"observation":"DI resolution showing up in a CPU profile at ~1K samples suggests either transient services being resolved per-request with non-trivial construction, or a deep dependency graph. Consider scoping or caching expensive services."},{"method":"System.Text.Json (aggregate serialization)","inclusivePct":1.2,"exclusivePct":0.6,"callChain":["Controller.Action","JsonSerializer.SerializeAsync","ObjectDefaultConverter.OnTryWrite","JsonPropertyInfo.GetMemberAndWriteJson","Utf8JsonWriter.WriteStringMinimized"],"observation":"JSON serialization aggregate (ObjectDefaultConverter 976, JsonWriterHelper.ToUtf8 870, OptimizedInboxTextEncoder 850, WriteStack.Push 409, WriteStringMinimized 384, JsonConverter.TryWrite 418) is the second-largest managed CPU consumer after SQL. Large response payloads with many string fields drive this. Source generators or DTO projection would help."}],"summary":"CPU time is dominated by SQL data reading and JSON response serialization. The TDS parser and SqlDataReader together consume the largest share of managed CPU — methods like TryReadChar, TryReadColumnInternal, PrepareAsyncInvocation, and Unicode chunk reading indicate the API is fetching large result sets with many string/nvarchar columns from SQL Server. JSON serialization (StringConverter.Write, ObjectDefaultConverter, Utf8JsonWriter) is the second major consumer, confirming that large object graphs with many string properties are being serialized into responses. The most impactful optimization would be to add EF Core .Select() projections to return only the fields clients need, combined with .AsNoTracking() — this would cut both the SQL read cost and the JSON serialization cost simultaneously. Consider also using System.Text.Json source generators for the response DTOs."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.9,"gen1Rate":0.79,"gen2Rate":0.04,"pauseTimeMs":{"avg":6.5,"max":91.7,"total":1351.2},"gcPauseRatio":1.1,"fragmentationPct":0.0,"observations":["Gen1 collection count (95) is nearly equal to Gen0 (108), meaning ~88% of Gen0 survivors get promoted — objects have medium lifetimes that escape Gen0 but die in Gen1. This pattern is typical of per-request allocations that live across async awaits or are held by buffers/caches with short TTLs.","Max GC pause of 91.7ms on a Gen0 collection is abnormally high — Gen0 pauses should be <10ms. This likely indicates a large Gen0 budget or many pinned objects forcing the GC to do extra work, and directly contributes to the 544ms p95 latency.","Gen2 collections are healthy at only 5 total with max 3.6ms pause — long-lived objects are well-managed and LOH pressure is minimal.","Total 208 GC events in the test window is moderate, but the combined 1.35s of pause time represents blocked thread time that inflates tail latencies.","The 1.1% GC pause ratio is below the 5% concern threshold, so GC is not dominating CPU time — but the max pause spikes are the real latency concern, not average throughput."]},"heapAnalysis":{"peakSizeMB":1063.04,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 1,063 MB (over 1GB) is very large for an API workload at ~1,087 RPS — this suggests either large object graphs being materialized per request (e.g., unbounded query results, large DTOs) or a caching layer holding significant data in memory.","Total allocation volume of 50,358 MB over the test window equates to ~420 MB/sec allocation rate — this is extremely high and is the primary driver of GC frequency. Reducing allocation volume is the single most impactful optimization.","Zero fragmentation indicates the LOH is not a concern and the GC is compacting effectively."]},"topAllocators":[{"type":"(allocation type data unavailable)","allocMB":null,"pctOfTotal":null,"callSite":"unknown — PerfView allocation tick data was not captured or export produced no type breakdown","observation":"Without allocation type data, root cause analysis is limited. Re-run with /DotNetAllocSampled and export the 'GC Heap Alloc Stacks' view to identify which types and call sites are responsible for the ~420 MB/sec allocation rate. Common culprits at this volume: large byte[] or string allocations from serialization, unbounded LINQ materializations (ToList on large result sets), or EF Core change tracker overhead."}],"summary":"The API is allocating ~420 MB/sec (50+ GB total), driving 208 GC collections with a worst-case 91.7ms pause that directly inflates p95 latency. The near-1:1 Gen0-to-Gen1 promotion ratio suggests per-request objects surviving just long enough to escape Gen0 — likely EF Core entity materialization, large response serialization buffers, or unconstrained query results. The #1 fix is to reduce allocation volume: add pagination or limit result set sizes, use object pooling (ArrayPool<T>) for serialization buffers, and consider projection queries (Select) instead of full entity materialization. Re-collecting with allocation type data enabled is strongly recommended to pinpoint the exact hotspot types and call sites."}
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
