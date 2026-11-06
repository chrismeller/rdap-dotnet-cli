using System.Net;
using System.Net.Http.Headers;
using Rdap.Bootstrap;

namespace Rdap.Tests;

public class RdapClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RdapClient CreateClient(FakeHandler handler, out HttpClient http)
    {
        http = new HttpClient(handler);
        return new RdapClient(http, new BootstrapService(http, cacheDirectory: null));
    }

    [Fact]
    public async Task Bootstraps_and_queries()
    {
        var handler = new FakeHandler().WithBootstrap()
            .Fixture("https://rdap.verisign.com/com/v1/domain/google.com", "domain-verisign-google.json");
        var client = CreateClient(handler, out var http);
        using (http)
        {
            var result = await client.QueryAsync(RdapQuery.Parse("Google.com"), cancellationToken: Ct);

            Assert.Null(result.ParseError);
            Assert.Equal("GOOGLE.COM", Assert.IsType<Models.Domain>(result.Data).LdhName);
            Assert.Equal(new Uri("https://rdap.markmonitor.com/rdap/domain/GOOGLE.COM"), RdapClient.FindReferral(result));
        }
    }

    [Fact]
    public async Task Explicit_server_skips_bootstrap()
    {
        var handler = new FakeHandler { FailUnknown = true }
            .Fixture("https://rdap.arin.net/registry/entity/GOGL", "entity-arin-gogl.json");
        var client = CreateClient(handler, out var http);
        using (http)
        {
            var result = await client.QueryAsync(RdapQuery.Parse("GOGL"), new Uri("https://rdap.arin.net/registry"), Ct);

            Assert.IsType<Models.Entity>(result.Data);
            Assert.Single(handler.Requests);
        }
    }

    [Fact]
    public async Task Not_found_throws_with_status()
    {
        var client = CreateClient(new FakeHandler().WithBootstrap(), out var http);
        using (http)
        {
            var ex = await Assert.ThrowsAsync<RdapHttpException>(() => client.QueryAsync(RdapQuery.Parse("nope.com"), cancellationToken: Ct));

            Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
            Assert.Contains("The requested domain was not found.", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Rate_limit_reports_retry_after()
    {
        var handler = new FakeHandler().WithBootstrap().Respond("https://rdap.arin.net/registry/ip/8.8.8.8", () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });
        var client = CreateClient(handler, out var http);
        using (http)
        {
            var ex = await Assert.ThrowsAsync<RdapHttpException>(() => client.QueryAsync(RdapQuery.Parse("8.8.8.8"), cancellationToken: Ct));

            Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
            Assert.Contains("retry after 30s", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Rate_limit_with_past_retry_after_date_is_clamped_to_zero()
    {
        var handler = new FakeHandler().WithBootstrap().Respond("https://rdap.arin.net/registry/ip/8.8.8.8", () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-5));
            return response;
        });
        var client = CreateClient(handler, out var http);
        using (http)
        {
            var ex = await Assert.ThrowsAsync<RdapHttpException>(() => client.QueryAsync(RdapQuery.Parse("8.8.8.8"), cancellationToken: Ct));

            Assert.Equal(TimeSpan.Zero, ex.RetryAfter);
            Assert.Contains("retry after 0s", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Unparseable_body_is_returned_with_parse_error()
    {
        var handler = new FakeHandler().WithBootstrap().Json("https://rdap.arin.net/registry/autnum/15169", """{"objectClassName":"autnum","events":"bad"}""");
        var client = CreateClient(handler, out var http);
        using (http)
        {
            var result = await client.QueryAsync(RdapQuery.Parse("AS15169"), cancellationToken: Ct);

            Assert.Null(result.Data);
            Assert.NotNull(result.ParseError);
            Assert.Contains("\"bad\"", result.Json, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("rdap.example.test")]
    [InlineData("GOGL")]
    public async Task No_server_throws(string input)
    {
        var client = CreateClient(new FakeHandler().WithBootstrap(), out var http);
        using (http)
        {
            await Assert.ThrowsAsync<RdapNoServerException>(() => client.QueryAsync(RdapQuery.Parse(input + (input.Contains('.') ? "x" : "")), cancellationToken: Ct));
        }
    }

    [Fact]
    public void Self_links_are_not_referrals()
    {
        var json = Fixture.Read("domain-markmonitor-google.json");
        var result = new RdapResult(new Uri("https://rdap.markmonitor.com/rdap/domain/google.com"), json, Serialization.RdapResponseParser.Parse(json), null);

        Assert.Null(RdapClient.FindReferral(result));
    }
}
