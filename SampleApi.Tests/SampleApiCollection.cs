using Xunit;

namespace SampleApi.Tests;

/// <summary>
/// Defines a shared test collection so that all test classes share a single
/// SampleApiFactory instance. This prevents concurrent DB drop/recreate conflicts
/// when multiple test classes use the same LocalDB test database.
/// </summary>
[CollectionDefinition("SampleApi")]
public class SampleApiCollection : ICollectionFixture<SampleApiFactory>
{
}
