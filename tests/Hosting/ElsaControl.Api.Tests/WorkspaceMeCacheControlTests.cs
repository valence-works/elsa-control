using System.Net;

namespace ElsaControl.Api.Tests;

public sealed class WorkspaceMeCacheControlTests : IClassFixture<DefaultControlApiTestApplicationFixture>
{
    private readonly ControlApiTestApplication _app;

    public WorkspaceMeCacheControlTests(DefaultControlApiTestApplicationFixture fixture) => _app = fixture.Application;

    [Theory]
    [InlineData("/api/me/workspaces")]
    [InlineData("/api/me/organizations")]
    [InlineData("/api/me/workspace-memberships")]
    public async Task Me_read_sets_private_no_store_cache_control(string path)
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var client = _app.CreateControlIdentityClient(subject: $"me-cache-{path.Trim('/').Replace('/', '-')}");

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
}
