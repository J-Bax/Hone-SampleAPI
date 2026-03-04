# Sample API

A .NET 6 Web API and Razor Pages marketplace that serves as the **optimization target** for the Hone harness. It exposes a product catalog, reviews, orders, and shopping cart — all backed by SQL Server LocalDB and Entity Framework Core 6.

## API Endpoints

### Products & Categories

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/products` | List all products |
| GET | `/api/products/{id}` | Get a single product |
| GET | `/api/products/by-category/{name}` | Filter by category |
| GET | `/api/products/search?q=term` | Search products |
| POST | `/api/products` | Create a product |
| PUT | `/api/products/{id}` | Update a product |
| DELETE | `/api/products/{id}` | Delete a product |
| GET | `/api/categories` | List all categories |
| GET | `/api/categories/{id}` | Get category with its products |
| GET | `/health` | Health check |

### Reviews

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/reviews` | List all reviews |
| GET | `/api/reviews/{id}` | Get a single review |
| GET | `/api/reviews/by-product/{productId}` | Reviews for a product |
| GET | `/api/reviews/average/{productId}` | Average rating |
| POST | `/api/reviews` | Create a review |
| DELETE | `/api/reviews/{id}` | Delete a review |

### Orders

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/orders` | List all orders |
| GET | `/api/orders/{id}` | Order with items |
| GET | `/api/orders/by-customer/{name}` | Orders by customer |
| POST | `/api/orders` | Create order |
| PUT | `/api/orders/{id}/status` | Update order status |

### Cart

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/cart/{sessionId}` | Get cart items |
| POST | `/api/cart` | Add item to cart |
| PUT | `/api/cart/{id}` | Update item quantity |
| DELETE | `/api/cart/{id}` | Remove single cart item |
| DELETE | `/api/cart/session/{sessionId}` | Clear cart |

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

E2E tests use `WebApplicationFactory` with a dedicated test database (`HoneSampleDb_Tests`):

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

