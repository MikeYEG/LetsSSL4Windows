using System.Net;
using LetsSSL.Core.Dns;
using Xunit;

namespace LetsSSL.Core.Tests;

public class DnsProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;
        public StubHandler(HttpStatusCode code, string body) { _code = code; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(_code) { Content = new StringContent(_body) });
    }

    [Fact]
    public async Task Cloudflare_verify_reports_active_token_as_valid()
    {
        var http = new HttpClient(new StubHandler(HttpStatusCode.OK,
            "{\"success\":true,\"result\":{\"status\":\"active\"}}"));
        using var provider = new CloudflareDnsProvider("token", http);

        var result = await provider.VerifyCredentialsAsync();

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Cloudflare_verify_reports_invalid_token_as_failure()
    {
        var http = new HttpClient(new StubHandler(HttpStatusCode.Unauthorized,
            "{\"success\":false,\"errors\":[{\"message\":\"Invalid API Token\"}]}"));
        using var provider = new CloudflareDnsProvider("bad", http);

        var result = await provider.VerifyCredentialsAsync();

        Assert.False(result.Success);
        Assert.Contains("Invalid API Token", result.Message);
    }

    [Fact]
    public async Task Cloudflare_verify_reports_inactive_token_as_failure()
    {
        var http = new HttpClient(new StubHandler(HttpStatusCode.OK,
            "{\"success\":true,\"result\":{\"status\":\"disabled\"}}"));
        using var provider = new CloudflareDnsProvider("token", http);

        var result = await provider.VerifyCredentialsAsync();

        Assert.False(result.Success);
        Assert.Contains("disabled", result.Message);
    }
}
