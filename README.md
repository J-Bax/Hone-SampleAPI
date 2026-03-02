# Sample API

A .NET 6 Web API and Razor Pages marketplace that serves as the **optimization target** for the Autotune harness. It exposes a product catalog, reviews, orders, and shopping cart — all backed by SQL Server LocalDB and Entity Framework Core 6.

This API **intentionally contains suboptimal patterns** — N+1 queries, missing indexes, no caching, no pagination, one-by-one deletes — so the agentic loop has real performance issues to discover and fix.

## API Endpoints

### Products & Categories

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/products` | List all products (no pagination) |
| GET | `/api/products/{id}` | Get a single product |
| GET | `/api/products/by-category/{name}` | Filter by category (N+1 pattern) |
| GET | `/api/products/search?q=term` | Search products (in-memory filter) |
| POST | `/api/products` | Create a product |
| PUT | `/api/products/{id}` | Update a product |
| DELETE | `/api/products/{id}` | Delete a product |
| GET | `/api/categories` | List all categories |
| GET | `/api/categories/{id}` | Get category with its products |
| GET | `/health` | Health check |

### Reviews

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/reviews` | List all reviews (no pagination) |
| GET | `/api/reviews/{id}` | Get a single review |
| GET | `/api/reviews/by-product/{productId}` | Reviews for a product (loads all, filters in memory) |
| GET | `/api/reviews/average/{productId}` | Average rating (loads all reviews to compute) |
| POST | `/api/reviews` | Create a review |
| DELETE | `/api/reviews/{id}` | Delete a review |

### Orders

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/orders` | List all orders (no pagination) |
| GET | `/api/orders/{id}` | Order with items (N+1 product lookups) |
| GET | `/api/orders/by-customer/{name}` | Orders by customer (loads all, filters in memory) |
| POST | `/api/orders` | Create order (N+1 price lookups) |
| PUT | `/api/orders/{id}/status` | Update order status |

### Cart

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/cart/{sessionId}` | Get cart items (N+1 product lookups) |
| POST | `/api/cart` | Add item to cart (loads all to check duplicates) |
| PUT | `/api/cart/{id}` | Update item quantity |
| DELETE | `/api/cart/{id}` | Remove single cart item |
| DELETE | `/api/cart/session/{sessionId}` | Clear cart (deletes one-by-one) |

## Razor Pages (Web Frontend)

A server-rendered Bootstrap 5 UI simulating a real marketplace:

| Route | Page | Description |
|-------|------|-------------|
| `/` | Home | Hero banner, featured products, recent reviews |
| `/Products` | Browse | Category filter, search, paginated grid |
| `/Products/Detail?id=N` | Detail | Product info, reviews, add to cart |
| `/Cart` | Cart | View/update/remove items, proceed to checkout |
| `/Checkout` | Checkout | Order summary, place order |
| `/Orders` | History | Lookup orders by customer name |

## Prerequisites

- .NET 6 SDK (`dotnet --version`)
- SQL Server LocalDB (`sqllocaldb info`)

## Run

```powershell
dotnet run --project SampleApi/
```

The API starts at `http://localhost:5000`. Swagger UI is available at `/swagger` in development mode. The web frontend is at `http://localhost:5000/`.

## Test

E2E tests use `WebApplicationFactory` with a dedicated test database (`AutotuneSampleDb_Tests`):

```powershell
dotnet test SampleApi.Tests/
```

43 tests cover all API endpoints and Razor Pages (14 products/categories, 8 reviews, 7 orders, 7 cart, 7 Razor Pages).

## Database

On first run, the database is auto-created and seeded with:

- **10 categories** and **1,000 products**
- **~2,000 reviews** across the first 500 products
- **100 orders** with 1–5 items each

To reset:

```powershell
sqllocaldb stop MSSQLLocalDB
sqllocaldb delete MSSQLLocalDB
sqllocaldb create MSSQLLocalDB
sqllocaldb start MSSQLLocalDB
```

## Domain Model

```
Category 1──* Product 1──* Review
                  │
                  ├──* OrderItem *──1 Order
                  │
                  └──* CartItem (keyed by SessionId)
```

## Intentional Performance Issues

These exist as optimization targets for the Autotune harness:

- **N+1 queries** — `by-category` loads all categories then all products; order detail fetches each product individually; cart does individual product lookups per item
- **No indexes** — only primary keys; no indexes on foreign keys (`ProductId`, `OrderId`), `Category`, or `Name`
- **No caching** — every request hits the database
- **No pagination** — `GET /api/products`, `GET /api/reviews`, `GET /api/orders` return all rows
- **In-memory filtering** — search, by-category, by-product, and by-customer all load full tables then filter in C#
- **One-by-one deletes** — clearing a cart calls `SaveChanges()` per item instead of batch delete
- **Redundant recomputation** — creating a review triggers a full average recompute
- **No eager loading** — related data fetched via separate queries instead of `.Include()`
