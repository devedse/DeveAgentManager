#pragma warning disable xUnit1051
using System.Net;
using System.Text.RegularExpressions;

namespace DeveAgentManager.Tests;

internal static class AccountForms
{
    internal static async Task<HttpResponseMessage> PostAccountAsync(this HttpClient client, string path, HttpContent? content, CancellationToken cancellationToken = default)
    {
        var page = await client.GetAsync("/login");
        if (page.StatusCode == HttpStatusCode.Redirect && page.Headers.Location is { } redirected)
            page = await client.GetAsync(redirected);
        var html = await page.Content.ReadAsStringAsync();
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        if (token.Length == 0) throw new InvalidOperationException("Login form did not render an antiforgery token.");
        var values = new Dictionary<string, string>();
        if (content is not null)
            foreach (var pair in (await content.ReadAsStringAsync()).Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                values[WebUtility.UrlDecode(parts[0])] = parts.Length == 2 ? WebUtility.UrlDecode(parts[1]) : "";
            }
        values["__RequestVerificationToken"] = token;
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(values) };
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        if (page.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var existing = client.DefaultRequestHeaders.TryGetValues("Cookie", out var current) ? string.Join("; ", current) : "";
            request.Headers.Add("Cookie", string.Join("; ", new[] { existing }.Concat(cookies.Select(c => c.Split(';')[0])).Where(c => c.Length > 0)));
        }
        return await client.SendAsync(request);
    }

    internal static async Task<HttpResponseMessage> ExchangeEditorAsync(this HttpClient client, Uri auth)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, auth.GetLeftPart(UriPartial.Path))
        { Content = new StringContent(auth.Fragment.TrimStart('#'), System.Text.Encoding.UTF8, "application/x-www-form-urlencoded") };
        request.Headers.Add("Origin", auth.GetLeftPart(UriPartial.Authority));
        return await client.SendAsync(request);
    }
}
