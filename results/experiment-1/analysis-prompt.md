Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 1)
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


## Diagnostic Profiling Reports
(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)

### cpu-hotspots
```json
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":1.84,"exclusivePct":1.84,"callChain":["EF Core Query","SqlDataReader.TryReadColumnInternal","TdsParser.TryReadSqlValue","TdsParserStateObject.TryReadChar"],"observation":"Top managed-code hotspot — the TDS protocol character reader is called an enormous number of times, indicating the API is fetching a very large volume of string-heavy rows from SQL Server. Reducing result set size (pagination, projection, filtering) would directly cut this cost."},{"method":"SortedDictionary/SortedSet Enumeration (aggregate)","inclusivePct":5.21,"exclusivePct":5.21,"callChain":["Application or EF Code","SortedDictionary.GetEnumerator","SortedSet.Enumerator.MoveNext","SortedSet.Enumerator.Initialize","Stack<T>..ctor"],"observation":"SortedDictionary and SortedSet enumeration collectively consumes ~5.2% of all CPU — an abnormally high amount. SortedSet uses a tree-walk with Stack allocation on every enumeration (388 samples in Stack..ctor, 229 in Pop). If ordering is not required, replacing SortedDictionary with Dictionary and SortedSet with HashSet would eliminate this O(log n) overhead and the per-enumeration allocations."},{"method":"System.Runtime.CompilerServices.CastHelpers.ChkCastAny","inclusivePct":1.76,"exclusivePct":1.76,"callChain":["EF Core Materialization","SingleQueryingEnumerable.MoveNextAsync","dynamicClass.lambda_method","CastHelpers.ChkCastAny"],"observation":"Type-casting overhead (ChkCastAny 3011 + ChkCastInterface 2000 + IsInstanceOfClass 858 = ~3.4% combined) is driven by EF Core materializing a very large number of polymorphic entities per request. Fewer entities materialized = fewer casts."},{"method":"Microsoft.EntityFrameworkCore.ChangeTracking.Internal.NavigationFixer.InitialFixup","inclusivePct":0.29,"exclusivePct":0.29,"callChain":["ToListAsync","SingleQueryingEnumerable.MoveNextAsync","StateManager.StartTrackingFromQuery","NavigationFixer.InitialFixup"],"observation":"EF Core change-tracking overhead: NavigationFixer (489) + StateManager.StartTrackingFromQuery (408) + InternalEntityEntry..ctor (399) + GetOrCreateIdentityMap (349) + MarkShadowPropertiesNotSet (266) totals ~1.1%. Using .AsNoTracking() on read-only queries would eliminate this entire cost."},{"method":"System.Text.UnicodeEncoding.GetCharCount","inclusivePct":1.65,"exclusivePct":1.65,"callChain":["TdsParser.TryReadSqlStringValue","String.CreateStringFromEncoding","UnicodeEncoding.GetCharCount"],"observation":"Unicode string decoding (GetCharCount 2819 + GetChars 1305 + CreateStringFromEncoding 495 = 2.7%) is driven by reading many nvarchar columns. Projecting only needed columns (SELECT specific fields instead of SELECT *) and reducing row count would cut this proportionally."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1.MoveNextAsync","inclusivePct":0.67,"exclusivePct":0.67,"callChain":["ToListAsync","ConfiguredCancelableAsyncEnumerable.MoveNextAsync","SingleQueryingEnumerable.MoveNextAsync"],"observation":"The EF Core row-iteration loop itself costs 1141 samples exclusively, plus ToListAsync at 442. Combined with all downstream SqlDataReader work, this is the main driver of the 7.5s p95 latency — queries are returning far too many rows to materialize into a single list."},{"method":"System.Collections.Generic.Dictionary`2.FindValue","inclusivePct":0.96,"exclusivePct":0.96,"callChain":["EF Core StateManager / Identity Resolution","Dictionary.FindValue"],"observation":"Dictionary lookups (FindValue 1163+478 + TryInsert 523+214 = 2378 total, ~1.4%) are largely from EF Core's identity map resolving tracked entities. High volume confirms many entities are being tracked per request — AsNoTracking would reduce this."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.TryReadColumnInternal","inclusivePct":1.05,"exclusivePct":1.05,"callChain":["SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","SqlDataReader.TryReadColumnInternal"],"observation":"The column reader (1803 samples) plus PrepareAsyncInvocation (1057), CheckDataIsReady (368), WillHaveEnoughData (654) show heavy per-column-per-row overhead. Many columns are being read per row — use projection (.Select) to fetch only required fields."},{"method":"System.Text.Json.Serialization.Converters.StringConverter.Write","inclusivePct":0.28,"exclusivePct":0.28,"callChain":["Kestrel Response","JsonSerializer.Serialize","StringConverter.Write"],"observation":"JSON serialization is only 0.28% of CPU — the bottleneck is firmly on the data-reading side, not response writing. However, the presence of many string writes confirms large response payloads that could benefit from pagination."}],"summary":"The CPU profile is overwhelmingly dominated by SQL data reading and entity materialization — the application is fetching massive result sets from SQL Server, materializing every row with full change tracking, and serializing everything to JSON. The top actionable optimizations are: (1) Add server-side pagination to limit rows returned per request, which would cut the dominant TDS parsing, string decoding, and EF Core materialization costs; (2) Replace SortedDictionary/SortedSet with Dictionary/HashSet if sort order is not required, eliminating 5.2% of CPU spent on tree-walk enumeration and per-iteration Stack allocations; (3) Add .AsNoTracking() to read-only queries to eliminate ~1.1% of CPU in change-tracking overhead; (4) Use .Select() projections to fetch only needed columns, reducing string decoding and column-reading overhead. The 7.5s p95 latency strongly suggests an N+1 or unbounded query pattern — addressing result set size should be the first priority."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":3,"gen1Rate":12,"gen2Rate":25,"pauseTimeMs":{"avg":3055.0,"max":8882.8,"total":122271.8},"gcPauseRatio":84.8,"fragmentationPct":0.0,"observations":["GC generation distribution is severely inverted: Gen2 (25) > Gen1 (12) > Gen0 (3). Normal apps show Gen0 >> Gen1 >> Gen2. This indicates most allocations are either large objects (>85KB) going directly to LOH/Gen2, or objects survive Gen0/Gen1 so quickly that Gen2 is triggered constantly.","GC pause ratio of 84.8% is catastrophic — the application spends 85% of its wall-clock time paused in GC, leaving only 15% for actual request processing. This single-handedly explains the 7546ms p95 latency.","Gen1 pauses average 5214ms with a max of 8882ms — these are full blocking GCs, not background collections. The runtime is likely falling back to blocking Gen2 GCs due to memory pressure exceeding the background GC's ability to keep up.","Gen0 collections are nearly absent (only 3) with 475ms average pause, suggesting ephemeral generations are being bypassed — allocations are too large or too long-lived for the nursery.","Total GC pause of 122 seconds means the service was frozen for over 2 minutes during the load test. Every request in flight during a GC pause experiences multi-second stalls."]},"heapAnalysis":{"peakSizeMB":2282.8,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 2.28GB is extremely large for an API service. Scanning and compacting a 2GB+ heap during blocking GC explains the multi-second pause times — GC pause duration scales with live heap size.","Total allocations of 37.3GB during the test with a 2.28GB peak means the app allocated and discarded roughly 16x its peak heap — massive allocation churn driving constant GC activity.","Fragmentation is 0%, so LOH fragmentation and pinning are not contributing factors. The problem is pure allocation volume and live object retention."]},"topAllocators":[{"type":"(allocation type data unavailable)","allocMB":37302.11,"pctOfTotal":100.0,"callSite":"Unknown — PerfView allocation tick data was not captured or topTypes is empty","observation":"Re-run PerfView with /DotNetAllocSampled and export GC Heap Alloc Stacks to identify the specific types and call sites responsible for the 37.3GB of allocations. Without this data, focus on the patterns below based on the GC behavior signature."}],"inferredAllocationPatterns":[{"pattern":"Large Object Heap allocations","evidence":"Inverted generation counts (Gen2 >> Gen0) strongly suggest frequent allocations above 85KB going directly to LOH, triggering Gen2 collections. Common culprits: large arrays, byte[] buffers for serialization, large string concatenations, List<T> resizing.","fix":"Use ArrayPool<T>.Shared.Rent()/Return() for temporary buffers. Use RecyclableMemoryStream instead of MemoryStream. Pre-size collections to avoid List<T>/Dictionary<K,V> resize-and-copy doubling."},{"pattern":"Object retention preventing background GC","evidence":"Blocking GC pauses averaging 2-5 seconds indicate the background GC cannot keep up, forcing the runtime into stop-the-world collections. Objects are likely held alive across multiple GC generations by long-lived references.","fix":"Review controller actions and middleware for objects cached in static fields, ConcurrentDictionary caches without eviction, or DbContext instances held too long. Ensure scoped services are truly scoped."},{"pattern":"Excessive per-request allocation volume","evidence":"37.3GB total allocations over the test at 125.5 RPS implies ~297MB allocated per second or ~2.4MB per request — an order of magnitude above typical API workloads.","fix":"Profile with dotnet-trace or PerfView allocation sampling to find the hot allocating call sites. Look for: materialized EF queries loading entire tables (use .AsNoTracking(), pagination, projection with .Select()), repeated serialization/deserialization, string formatting in loops."}],"summary":"The application is in a GC crisis: 84.8% of execution time is spent in garbage collection, with blocking pauses up to 8.8 seconds that directly cause the 7546ms p95 latency. The inverted GC generation pattern (Gen2=25, Gen1=12, Gen0=3) and 2.28GB peak heap indicate massive large-object or long-lived allocations overwhelming the GC. The #1 priority is reducing allocation volume — likely caused by unbounded EF Core query materialization, large buffer allocations, or uncapped in-memory caching. Use ArrayPool<T> for buffers, add pagination/projection to EF queries, and enable Server GC as an immediate mitigation while root-causing the allocation hotspots with PerfView allocation sampling."}
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
