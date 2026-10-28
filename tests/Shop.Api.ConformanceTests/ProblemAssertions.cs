using System.Text.Json;
using Xunit;

namespace Shop.Api.ConformanceTests;

internal static class ProblemAssertions
{
    public static async Task<JsonElement> VerifyAsync(HttpResponseMessage response, int status, string code, string path)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var problem = document.RootElement;
        Assert.Equal(status, problem.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(path, problem.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        Assert.True(Uri.TryCreate(problem.GetProperty("type").GetString(), UriKind.Absolute, out _));
        Assert.False(problem.TryGetProperty("exception", out _));
        return problem.Clone();
    }
}
