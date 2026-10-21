using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;
using Xunit;

namespace Nocturne.Tests;

public class TraktAuthTests
{
    [Fact]
    public void Pkce_GeneratesValidVerifierAndChallenge()
    {
        var verifier = PkceHelper.GenerateCodeVerifier();
        Assert.NotNull(verifier);
        Assert.InRange(verifier.Length, 43, 128);

        var challenge = PkceHelper.GenerateCodeChallenge(verifier);
        Assert.NotNull(challenge);

        // Manually compute SHA256 base64url
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
        var expectedChallenge = Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        Assert.Equal(expectedChallenge, challenge);
    }

    [Fact]
    public void CredentialProtection_RoundTripsData()
    {
        var original = Encoding.UTF8.GetBytes("SuperSecretToken12345!@#$");
        var encrypted = CredentialProtection.Protect(original);
        Assert.NotNull(encrypted);

        var decrypted = CredentialProtection.Unprotect(encrypted);
        Assert.NotNull(decrypted);
        Assert.Equal(original, decrypted);
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public MockHttpMessageHandler(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"access_token\":\"tok123\",\"refresh_token\":\"ref456\",\"expires_in\":7200}", TraktAuthStatus.Authorized)]
    [InlineData(HttpStatusCode.BadRequest, "{}", TraktAuthStatus.Pending)]
    [InlineData(HttpStatusCode.NotFound, "{}", TraktAuthStatus.NotFound)]
    [InlineData(HttpStatusCode.Conflict, "{}", TraktAuthStatus.AlreadyUsed)]
    [InlineData(HttpStatusCode.Gone, "{}", TraktAuthStatus.Expired)]
    [InlineData((HttpStatusCode)418, "{}", TraktAuthStatus.Denied)]
    [InlineData((HttpStatusCode)429, "{}", TraktAuthStatus.SlowDown)]
    public async Task PollForToken_MapsHttpStatusCodesCorrectly(HttpStatusCode statusCode, string json, TraktAuthStatus expectedStatus)
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"trakt_auth_test_{Guid.NewGuid():N}.db");
        using var db = new DatabaseService(tempDb);
        var mockHttp = new HttpClient(new MockHttpMessageHandler(statusCode, json))
        {
            BaseAddress = new Uri("https://api.trakt.tv")
        };

        var auth = new TraktAuthService(db, mockHttp);
        var deviceCode = new DeviceCodeResponse("dcode", "ucode", "url", 600, 5, "verifier123");

        var result = await auth.PollForTokenAsync(deviceCode, CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
        if (expectedStatus == TraktAuthStatus.Authorized)
        {
            Assert.Equal("tok123", result.AccessToken);
            Assert.Equal(TraktConnectionState.Connected, auth.State);
        }

        try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
    }

    [Fact]
    public void ClientIdUsedForToken_InvalidationGuard()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"trakt_cid_test_{Guid.NewGuid():N}.db");
        using var db = new DatabaseService(tempDb);

        // Save state tied to a custom client ID
        var orig = Encoding.UTF8.GetBytes("tok_abc");
        var enc = CredentialProtection.Protect(orig);
        db.SetIntegrationState("trakt", "mode", "Custom");
        db.SetIntegrationState("trakt", "custom_client_id", "client_ABC");
        db.SetIntegrationState("trakt", "access_token_dpapi", Convert.ToHexString(enc));
        db.SetIntegrationState("trakt", "client_id_used", "client_ABC");
        db.SetIntegrationState("trakt", "expires_utc", (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600).ToString());

        var auth = new TraktAuthService(db);
        Assert.Equal(TraktConnectionState.Connected, auth.State);
        Assert.Equal("tok_abc", auth.GetAccessToken());

        // Now change custom_client_id to client_XYZ
        auth.SetCustomClientId("client_XYZ");

        // Token should be invalidated because client ID no longer matches
        Assert.Equal(TraktConnectionState.Disconnected, auth.State);
        Assert.Null(auth.GetAccessToken());

        try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
    }
}
