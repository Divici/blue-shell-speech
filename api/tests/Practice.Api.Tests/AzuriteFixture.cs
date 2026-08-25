using Azure.Storage.Blobs;
using Practice.Infrastructure.Storage;
using Testcontainers.Azurite;

namespace Practice.Api.Tests;

/// <summary>
/// Real blob storage, in a container, per test run.
///
/// Same reasoning as <see cref="SqlServerFixture"/> and the same rule from
/// docs/TEST_STRATEGY.md: the readiness probe's whole job is to find out whether this
/// replica can reach the container it needs, and a stub answers that question by
/// construction. Azurite is what <c>docker compose</c> already runs locally
/// (docker-compose.yml), so this is the same emulator the application is developed
/// against rather than a second fiction invented for the tests.
///
/// It carries no secret out of this repository: the connection string comes from the
/// running container at test time, and the well-known emulator key it contains is a public
/// constant rather than a credential.
///
/// SYNTHETIC ONLY, and in fact empty — the probe reads container metadata and never a
/// blob, so nothing is ever written here.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    private readonly AzuriteContainer _container =
        new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();

    /// <summary>An emulator connection string. Carries no managed identity and no key of ours.</summary>
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        /*
         * The container the probe reads has to exist, as it does in production —
         * infra/provision-platform.sh creates session-audio and public-resources when the
         * storage account is provisioned. A probe against an account with no containers is
         * a different test, and it is written separately in ReadinessCheckTests.
         */
        await new BlobServiceClient(ConnectionString)
            .GetBlobContainerClient(StorageContainers.SessionAudio)
            .CreateIfNotExistsAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}
