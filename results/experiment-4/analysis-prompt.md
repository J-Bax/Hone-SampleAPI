Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 4)
- p95 Latency: 7546.103045ms
- Requests/sec: 125.5
- Error rate: 0%
- Improvement vs baseline: 0%

## Baseline Performance
- p95 Latency: 7546.103045ms
- Requests/sec: 125.5
- Error rate: 0%


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

## Experiment History (with metrics)
Do NOT re-attempt optimizations that were already tried and resulted in stale or regressed outcomes. Propose different targets or approaches instead.
| Exp | File | Outcome | p95 (ms) | RPS | Branch |
|-----|------|---------|----------|-----|--------|
| 1 | — | invalid_target | N/A | N/A | hone/experiment-1 |
| 2 | — | invalid_target | N/A | N/A | hone/experiment-2 |
| 3 | — | invalid_target | N/A | N/A | hone/experiment-3 |


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"SampleApi.Pages.Orders.IndexModel.<OnGetAsync>b__2(OrderItem)","inclusivePct":0.4,"exclusivePct":0.4,"callChain":["OnGetAsync","lambda processing OrderItem collection"],"observation":"Only application-level code in the profile — a lambda iterating over OrderItems inside the Orders index page. With 7.5s p95 latency, this suggests the page loads a massive number of orders+items per request, triggering all downstream SQL and EF Core overhead."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.TryReadColumnInternal","inclusivePct":1.1,"exclusivePct":1.1,"callChain":["EF Core SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun","TryReadColumnInternal"],"observation":"Top exclusive-time SqlClient method. Combined with TryReadChar (8,295), TryGetTokenLength (4,535), TryProcessColumnHeaderNoNBC (3,767), and TryReadSqlValue (3,056), SqlClient column-reading accounts for ~35K+ samples — the application is reading an enormous number of rows and columns from SQL Server."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable.MoveNextAsync","inclusivePct":0.8,"exclusivePct":0.8,"callChain":["ToListAsync","SingleQueryingEnumerable.AsyncEnumerator.MoveNextAsync","SqlDataReader.ReadAsync"],"observation":"EF Core query materialization dominates async enumeration. The SingleQueryingEnumerable path (5,878 + 1,083 samples) combined with ToListAsync (1,966) indicates a single large query materializing thousands of entities into memory rather than paginated or projected results."},{"method":"Microsoft.EntityFrameworkCore.ChangeTracking.Internal.StateManager.StartTrackingFromQuery","inclusivePct":0.3,"exclusivePct":0.3,"callChain":["SingleQueryingEnumerable.MoveNextAsync","StateManager.StartTrackingFromQuery","InternalEntityEntry..ctor","NavigationFixer.InitialFixup"],"observation":"EF Core identity tracking overhead: StartTrackingFromQuery (2,552) + InternalEntityEntry ctor (2,063) + NavigationFixer.InitialFixup (1,685) + GetOrCreateIdentityMap (1,699) + MarkShadowPropertiesNotSet (1,433) = ~9,400 samples. Use .AsNoTracking() for read-only pages to eliminate this entire cost."},{"method":"System.Collections.Generic.SortedDictionary/SortedSet enumeration","inclusivePct":3.5,"exclusivePct":3.5,"callChain":["StateManager / NavigationFixer","SortedDictionary.ValueCollection.GetEnumerator","SortedSet.Enumerator.MoveNext"],"observation":"SortedSet/SortedDictionary operations total ~25K+ samples (MoveNext 5,139 + ValueCollection.MoveNext 4,364 + Enumerator.MoveNext 3,452 + get_Current 2,786 + GetEnumerator 2,211 + Initialize 2,199 + more). These are EF Core's internal identity map structures — cost is proportional to tracked entity count. Reducing entity count or disabling tracking eliminates this."},{"method":"System.Collections.Generic.Dictionary.FindValue / TryInsert","inclusivePct":1.8,"exclusivePct":1.8,"callChain":["EF Core StateManager / IdentityMap","Dictionary.FindValue","Dictionary.TryInsert"],"observation":"Dictionary lookups (FindValue 7,511 + 2,805) and inserts (TryInsert 2,433 + 1,160) total ~14K samples. Used by EF Core's identity resolution for every materialized entity. High volume confirms thousands of entities being tracked per request."},{"method":"System.Text.UnicodeEncoding.GetCharCount / GetChars","inclusivePct":2.0,"exclusivePct":2.0,"callChain":["TdsParser.TryReadSqlStringValue","UnicodeEncoding.GetCharCount","UnicodeEncoding.GetChars","String.CreateStringFromEncoding"],"observation":"Unicode string decoding (GetCharCount 11,634 + GetChars 3,983 + CreateStringFromEncoding 1,788) = ~17K samples. This is reading nvarchar/ntext columns from SQL Server. Indicates many string-heavy columns being read — use a projection (Select) to fetch only needed columns."},{"method":"System.Runtime.CompilerServices.CastHelpers (ChkCast/IsInstance)","inclusivePct":2.8,"exclusivePct":2.8,"callChain":["EF Core materialization / navigation fixup","CastHelpers.ChkCastInterface","CastHelpers.ChkCastAny"],"observation":"Type-check and cast operations total ~22K samples (ChkCastInterface 9,002 + ChkCastAny 7,967 + IsInstanceOfClass 2,717 + IsInstanceOfInterface 1,480). These are CLR overhead from EF Core's reflection-heavy entity materialization across thousands of entities."},{"method":"sqlmin/sqllang (SQL Server engine)","inclusivePct":8.1,"exclusivePct":8.1,"callChain":["SqlClient TdsParser.TryRun","Named Pipe I/O","sqlmin!?","sqllang!?"],"observation":"SQL Server engine internals (sqlmin 39,094 + sqllang 23,591 + sqldk 4,380 + sqltses 2,025) = ~69K samples. Server-side query execution is a major cost center. The query plan likely involves large table scans or missing indexes — profiling the SQL query independently is recommended."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.PrepareAsyncInvocation / ReadAsync","inclusivePct":0.8,"exclusivePct":0.8,"callChain":["ToListAsync","MoveNextAsync","SqlDataReader.ReadAsync","PrepareAsyncInvocation","ExecutionContext.Capture"],"observation":"Async overhead per row: PrepareAsyncInvocation (3,023) + ReadAsync (2,945) + ExecutionContext.Capture (1,843) + AsyncMethodBuilderCore.Start (2,007) + SetLocalValue (3,586) = ~13K samples. This per-row async machinery cost is amplified by reading thousands of rows. Fewer rows = proportionally less async overhead."}],"summary":"The CPU profile is dominated by reading and materializing a massive result set from SQL Server on the Orders index page. The single application-code hotspot — a lambda in OnGetAsync processing OrderItems — sits atop a pyramid of SqlClient row-reading (~35K samples), EF Core change tracking (~25K samples), SortedDictionary identity-map enumeration (~25K samples), and Unicode string decoding (~17K samples). The most impactful optimizations are: (1) Add pagination to the Orders query to avoid loading all orders at once, (2) Use .AsNoTracking() since this is a read-only page, (3) Use a projection (.Select) to fetch only the columns needed for display instead of full entity graphs, and (4) Investigate the SQL query plan for missing indexes. The 7.5s p95 latency is almost entirely caused by transferring and materializing too many entities per request."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.02,"gen1Rate":0.42,"gen2Rate":1.54,"pauseTimeMs":{"avg":83.2,"max":396.2,"total":20294.9},"gcPauseRatio":16.5,"fragmentationPct":0.0,"observations":["GC generation distribution is severely inverted: 189 Gen2 collections vs only 3 Gen0 and 52 Gen1. Normally Gen0 >> Gen1 >> Gen2. This indicates most allocations are either landing directly on the Large Object Heap (objects ≥85KB) or objects are being promoted to Gen2 almost immediately due to long-lived references holding them.","Gen2 collection rate of ~1.54/sec is extremely high — full heap collections are happening constantly, each scanning the entire 2.7GB heap. This is the dominant source of GC overhead.","Gen1 avg pause of 130ms and max of 396.2ms are severe. Gen2 avg pause of 70.8ms with max of 371.2ms confirms that GC is performing deep, expensive collections regularly.","GC pause ratio of 16.5% means the application spends roughly 1 out of every 6 seconds paused for garbage collection. This directly explains the inflated p95 latency of 7546ms — requests are being stalled by back-to-back Gen2 collections.","Total allocations of ~218.8GB over ~123s yields an effective allocation rate of ~1,779 MB/sec. The reported allocRateMBSec of 0.0 appears to be a reporting artifact. This allocation volume is extreme and is the root driver of the excessive GC activity.","The near-zero Gen0 count (3) with massive total allocations strongly suggests that most allocated objects are ≥85KB and go directly to the LOH, bypassing Gen0/Gen1 entirely and triggering Gen2 collections."]},"heapAnalysis":{"peakSizeMB":2698.4,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 2,698MB (~2.7GB) is extremely large for an API service. This indicates either massive in-memory data structures (e.g., loading entire database tables into memory), unbounded caching, or large response buffering.","With 218.8GB total allocated across ~123 seconds but a peak heap of only 2.7GB, the vast majority of allocations are short-to-medium lived — but they are large enough to land on the LOH and trigger Gen2 collections before being reclaimed.","Fragmentation is 0%, which means the LOH compaction is keeping up, but at the cost of expensive Gen2 collections."]},"topAllocators":[{"type":"(unavailable — no allocation type breakdown in PerfView data)","allocMB":null,"pctOfTotal":null,"callSite":null,"observation":"Allocation tick data was not captured or exported. To identify the specific types driving the ~1.78GB/sec allocation rate, re-run PerfView with /DotNetAllocSampled and export the 'GC Heap Alloc Stacks' view. Given the inverted generation profile (LOH-heavy), likely culprits are: large byte[] buffers from reading full database result sets, large string allocations from JSON serialization of oversized responses, or List<T> internal array resizing when collections grow beyond initial capacity."}],"summary":"The API is critically memory-bound: 218GB of allocations over 2 minutes with a 16.5% GC pause ratio and 189 Gen2 collections are the primary cause of the 7546ms p95 latency. The inverted GC generation profile (Gen2 >> Gen0) indicates most allocations are large objects (≥85KB) landing directly on the LOH — likely from loading full database result sets into memory or serializing oversized payloads. The #1 fix is to eliminate bulk data loading: use server-side pagination, streaming (IAsyncEnumerable), or projection queries that return only needed columns/rows, which will dramatically reduce both allocation volume and heap size, cutting Gen2 collections and GC pause time by an order of magnitude."}
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
