Analyze this Web API's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.

## Current Performance (Experiment 1)
- p95 Latency: 888.549155000001ms
- Requests/sec: 683.2
- Error rate: 0%
- Improvement vs baseline: 0%

## Baseline Performance
- p95 Latency: 888.549155000001ms
- Requests/sec: 683.2
- Error rate: 0%


## Traffic Distribution (k6 Scenario)
The following k6 load test scenario defines the request patterns and relative weights of each
endpoint. Use this to estimate what percentage of total traffic each endpoint/code path receives.

```javascript
import http from 'k6/http';
import { check } from 'k6';

// Baseline scenario: high-concurrency stress test exercising every endpoint.
// Same user-journey shape as a real marketplace session (browse → review →
// cart → order → pages) but with zero think-time so VUs fire requests
// back-to-back, creating real server contention.
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

  // ── Razor Pages ──

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
{"hotspots":[{"method":"Microsoft.Data.SqlClient.TdsParserStateObject.TryReadChar","inclusivePct":6.8,"exclusivePct":1.0,"callChain":["ToListAsync","SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TdsParser.TryRun","TdsParserStateObject.TryReadChar"],"observation":"Top leaf method in the SQL data reader pipeline. High exclusive sample count (9287) indicates the API is reading a very large volume of character/string data from SQL Server — likely fetching too many rows or too many string columns per query."},{"method":"Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1+AsyncEnumerator.MoveNextAsync","inclusivePct":14.2,"exclusivePct":0.4,"callChain":["Controller Action","ToListAsync","SingleQueryingEnumerable.MoveNextAsync"],"observation":"High inclusive percentage as the main EF Core query materialization loop. All SQL reading, entity construction, and change tracking flows through here. The sheer volume of samples beneath this method confirms queries are returning large result sets that are expensive to materialize."},{"method":"SortedSet/SortedDictionary Enumeration (EF Core Change Tracker)","inclusivePct":1.9,"exclusivePct":1.9,"callChain":["SingleQueryingEnumerable.MoveNextAsync","StateManager.StartTrackingFromQuery","NavigationFixer.InitialFixup","SortedDictionary.ValueCollection.Enumerator.MoveNext"],"observation":"Combined 17,152 exclusive samples across SortedSet/SortedDictionary enumerators. These are EF Core's internal change tracker data structures. The high cost indicates hundreds or thousands of entities being tracked per request — classic sign of missing AsNoTracking() on read-only queries or fetching far too many entities."},{"method":"System.Text.UnicodeEncoding.GetCharCount + GetChars","inclusivePct":1.7,"exclusivePct":1.7,"callChain":["TdsParser.TryReadSqlStringValue","UnicodeEncoding.GetCharCount / GetChars","String.CreateStringFromEncoding"],"observation":"16,046 exclusive samples decoding Unicode strings from the TDS wire protocol. This confirms the API transfers a large volume of string/nvarchar data from SQL Server. Consider selecting only needed columns (projection) or reducing nvarchar column sizes."},{"method":"System.Runtime.CompilerServices.CastHelpers (ChkCast/IsInstance)","inclusivePct":1.9,"exclusivePct":1.9,"callChain":["SingleQueryingEnumerable.MoveNextAsync","EF Core Materializer","CastHelpers.ChkCastAny/ChkCastInterface/IsInstanceOfClass"],"observation":"17,834 exclusive samples in CLR type-checking helpers. This overhead comes from EF Core materializing many entities through polymorphic code paths (boxing, interface dispatch). Proportional to the number of entities materialized — reducing result set size would reduce this."},{"method":"Microsoft.EntityFrameworkCore.ChangeTracking.Internal.NavigationFixer.InitialFixup","inclusivePct":0.7,"exclusivePct":0.14,"callChain":["StateManager.StartTrackingFromQuery","NavigationFixer.InitialFixup","SortedDictionary enumeration"],"observation":"EF Core navigation fixup wires up relationships between tracked entities. 1,287 samples plus all the SortedDictionary enumeration it drives. This is expensive when loading large graphs of related entities — use AsNoTracking() or split into focused queries."},{"method":"Microsoft.EntityFrameworkCore.ChangeTracking.Internal.StateManager.StartTrackingFromQuery","inclusivePct":0.6,"exclusivePct":0.12,"callChain":["SingleQueryingEnumerable.MoveNextAsync","StateManager.StartTrackingFromQuery","GetOrCreateIdentityMap","InternalEntityEntry..ctor"],"observation":"1,122 exclusive samples plus 1,018 in GetOrCreateIdentityMap and 962 in InternalEntityEntry constructor. Each entity returned from a query gets tracked individually. For read-only endpoints (GET requests), AsNoTracking() eliminates this entire cost."},{"method":"System.Collections.Generic.Dictionary`2.FindValue / TryInsert","inclusivePct":0.8,"exclusivePct":0.8,"callChain":["EF Core StateManager / IdentityMap","Dictionary.FindValue / TryInsert"],"observation":"7,682 combined exclusive samples in Dictionary operations. Used by EF Core's identity map to detect duplicate entities. Cost scales with number of tracked entities — further evidence of oversized result sets."},{"method":"Microsoft.Data.SqlClient.SqlDataReader.TryReadColumnInternal","inclusivePct":2.1,"exclusivePct":0.68,"callChain":["SingleQueryingEnumerable.MoveNextAsync","SqlDataReader.ReadAsync","TryReadColumnInternal","TdsParser.TryReadSqlValue"],"observation":"6,303 exclusive samples reading column data. Combined with GetString (947) and GetInt32 (931), the column-reading pipeline is hot. If queries use SELECT * instead of projecting specific columns, unnecessary columns add significant CPU cost here."},{"method":"System.Text.Json.Serialization.Converters.StringConverter.Write","inclusivePct":0.24,"exclusivePct":0.24,"callChain":["Controller Action","JsonSerializer.SerializeAsync","StringConverter.Write"],"observation":"2,183 samples serializing strings to JSON responses. Relatively modest but confirms string-heavy payloads. If the API returns large collections with many string properties, consider pagination or DTO projection to reduce response size."}],"summary":"The CPU profile is dominated by SQL data reading and EF Core entity materialization/change tracking, which together account for the majority of managed CPU time. The pattern strongly suggests queries returning oversized result sets — too many rows, too many columns, or both — with full change tracking enabled on read-only paths. The top optimizations are: (1) add AsNoTracking() to read-only queries to eliminate ~20% of managed CPU overhead from change tracking and navigation fixup, (2) add server-side pagination (Take/Skip) to limit result set size, and (3) use Select() projections to fetch only needed columns instead of full entities, which would reduce SQL reading, string decoding, and serialization costs simultaneously."}
```

### memory-gc
```json
{"gcAnalysis":{"gen0Rate":0.03,"gen1Rate":0.31,"gen2Rate":0.67,"pauseTimeMs":{"avg":490.2,"max":1293.8,"total":60791.0},"gcPauseRatio":49.8,"fragmentationPct":0.0,"observations":["CRITICAL: GC pause ratio is 49.8% — the application spends half its time in garbage collection, directly explaining the 888ms p95 latency","Generation distribution is severely inverted: Gen2 (82) >> Gen1 (38) >> Gen0 (4). Normal apps show Gen0 >> Gen1 >> Gen2. This indicates most allocations bypass Gen0/Gen1 entirely, landing directly on the Large Object Heap (LOH) or being promoted immediately to Gen2","Gen1 average pause of 695ms and max of 1293ms are catastrophic — single GC pauses alone exceed the p95 latency target","Gen2 collection count of 82 over ~122s means a full blocking GC roughly every 1.5 seconds — the runtime is under constant memory pressure","Total allocation volume is ~114 GB over ~122s (~935 MB/sec allocation rate) — this is extreme and suggests the hot path is materializing very large objects per request (e.g., unbounded query results, large serialization buffers, or full table scans into memory)"]},"heapAnalysis":{"peakSizeMB":2183.95,"avgSizeMB":null,"lohSizeMB":null,"observations":["Peak heap of 2.1 GB under load is extremely high for an API serving 683 req/sec — suggests each request holds ~3 MB of live objects or long-lived caches are growing unbounded","114 GB total allocation across ~122s confirms massive churn — objects are allocated and discarded at an unsustainable rate","Zero fragmentation is expected when the entire heap is constantly being collected and compacted due to the extreme GC pressure"]},"topAllocators":[{"type":"(allocation type data unavailable)","allocMB":null,"pctOfTotal":null,"callSite":"Unknown — PerfView allocation tick data was not captured or export failed","observation":"Re-run diagnostics with /DotNetAllocSampled and export 'GC Heap Alloc Stacks' view to identify the specific types driving 935 MB/sec allocation rate. Given the inverted generation pattern (Gen2 >> Gen0), prime suspects are: (1) Large arrays or List<T> buffers >85KB hitting LOH directly — likely from Entity Framework materializing unbounded query results (e.g., missing Take/pagination), (2) Large string concatenations from serialization, (3) byte[] buffers from response serialization of oversized payloads"}],"summary":"The application is in a GC crisis: 49.8% of execution time is spent collecting garbage, with 82 Gen2 collections in ~122 seconds and a peak heap of 2.1 GB. The inverted generation pattern (Gen2 >> Gen0) strongly indicates Large Object Heap allocations — likely from Entity Framework queries materializing unbounded result sets into large arrays or lists (>85KB each). The #1 fix is to add server-side pagination or `.Take(N)` limits to database queries to prevent large in-memory result sets, which will eliminate LOH pressure, dramatically reduce Gen2 collections, and should cut p95 latency from 888ms to well under 200ms."}
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
