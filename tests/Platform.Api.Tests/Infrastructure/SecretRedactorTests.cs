using Platform.Api.Infrastructure;

namespace Platform.Api.Tests.Infrastructure;

/// <summary>
/// Captured pipeline output is shown to everyone signed in, so secrets are redacted before it is
/// stored. The name of each setting stays — a reader still needs to see which one was involved — and
/// everything that isn't a secret reads exactly as the producer sent it. Every value here is fake.
/// </summary>
public class SecretRedactorTests
{
    private const string R = SecretRedactor.Marker;

    [Fact]
    public void KubectlDescribe_RedactsEnvValues_AndKeepsSecretReferences()
    {
        var describe = string.Join("\n",
            "    Environment:",
            "      ASPNETCORE_ENVIRONMENT:             Staging",
            "      PYRA_ServiceBus__ConnectionString:  Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=app;SharedAccessKey=FAKEKEY0123456789abcdef=",
            "      PYRA_Database__Password:            FAKE-DB-PASSWORD",
            "      FROM_SECRET:                        <set to the key 'k' in secret 's'>  Optional: false",
            "    Mounts:");

        var redacted = SecretRedactor.Redact(describe);

        Assert.DoesNotContain("FAKEKEY", redacted);
        Assert.DoesNotContain("FAKE-DB-PASSWORD", redacted);
        Assert.Contains($"      PYRA_ServiceBus__ConnectionString:  {R}", redacted);
        Assert.Contains($"      PYRA_Database__Password:            {R}", redacted);
        // Not secrets: an ordinary setting, and a reference that only names where the secret lives.
        Assert.Contains("      ASPNETCORE_ENVIRONMENT:             Staging", redacted);
        Assert.Contains("<set to the key 'k' in secret 's'>", redacted);
    }

    [Theory]
    [InlineData(
        "Endpoint=sb://x.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=FAKE+key/0123=",
        "Endpoint=sb://x.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=" + R)]
    [InlineData(
        "DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=FAKEaccountKEY==;EndpointSuffix=core.windows.net",
        "DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=" + R + ";EndpointSuffix=core.windows.net")]
    [InlineData(
        "Server=tcp:db.example.net,1433;User ID=app;Password=FAKE-pw,with;Encrypt=True",
        "Server=tcp:db.example.net,1433;User ID=app;Password=" + R + ";Encrypt=True")]
    [InlineData("Host=db;Username=app;Pwd=\"FAKE;quoted\";Port=5432", "Host=db;Username=app;Pwd=\"" + R + "\";Port=5432")]
    public void ConnectionStrings_LoseOnlyTheirSecretValues(string input, string expected) =>
        Assert.Equal(expected, SecretRedactor.Redact(input));

    [Theory]
    [InlineData("helm upgrade --set db.password=FAKEPW1 --set image.tag=1.2.3", "helm upgrade --set db.password=" + R + " --set image.tag=1.2.3")]
    [InlineData("export GITHUB_TOKEN=FAKETOKEN123", "export GITHUB_TOKEN=" + R)]
    [InlineData("https://api.example.com/items?api_key=FAKEAPIKEY&page=2", "https://api.example.com/items?api_key=" + R)]
    [InlineData("client_secret: FAKE-CLIENT-SECRET", "client_secret: " + R)]
    [InlineData("  - apiKey: 'FAKE-quoted-key'", "  - apiKey: '" + R + "'")]
    [InlineData("{\"user\":\"app\",\"password\":\"FAKE-json-pw\",\"port\":5432}", "{\"user\":\"app\",\"password\":\"" + R + "\",\"port\":5432}")]
    public void KeyValuePairs_NamingASecret_LoseTheirValue(string input, string expected) =>
        Assert.Equal(expected, SecretRedactor.Redact(input));

    [Fact]
    public void ManifestEnvEntries_LoseTheValueOfASecretNamedOnThePreviousLine()
    {
        var yaml = "        - name: PYRA_Database__Password\n          value: FAKE-manifest-pw\n        - name: LOG_LEVEL\n          value: Debug";
        var json = "{\"name\":\"DB_PASSWORD\",\"value\":\"FAKE-json-env\"},{\"name\":\"LOG_LEVEL\",\"value\":\"Debug\"}";

        Assert.Equal(
            $"        - name: PYRA_Database__Password\n          value: {R}\n        - name: LOG_LEVEL\n          value: Debug",
            SecretRedactor.Redact(yaml));
        Assert.Equal(
            $"{{\"name\":\"DB_PASSWORD\",\"value\":\"{R}\"}},{{\"name\":\"LOG_LEVEL\",\"value\":\"Debug\"}}",
            SecretRedactor.Redact(json));
    }

    [Theory]
    [InlineData("Authorization: Bearer FAKEbearerTOKEN0123456789", "Authorization: Bearer " + R)]
    [InlineData("curl -H \"Authorization: Basic RkFLRTpGQUtF\" https://x", "curl -H \"Authorization: Basic " + R + "\" https://x")]
    [InlineData("sending Bearer FAKEbearerTOKEN0123456789 upstream", "sending Bearer " + R + " upstream")]
    [InlineData("amqps://svc-user:FAKE-url-pw@broker.example.net:5671/vhost", "amqps://svc-user:" + R + "@broker.example.net:5671/vhost")]
    [InlineData("https://acct.blob.core.windows.net/c/f.zip?sv=2024-01-01&se=2026-12-01&sig=FAKEsig%2Babc%3D", "https://acct.blob.core.windows.net/c/f.zip?sv=2024-01-01&se=2026-12-01&sig=" + R)]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJmYWtlIn0.FAKEsignature_abc-123 used", "token " + R + " used")]
    [InlineData("cloning with ghp_FAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKE1 now", "cloning with " + R + " now")]
    public void TokensAndCredentialsInUrls_AreRedacted(string input, string expected) =>
        Assert.Equal(expected, SecretRedactor.Redact(input));

    [Fact]
    public void PrivateKeyBlocks_AreRedacted_EvenWhenTheEndMarkerIsMissing()
    {
        var whole = "before\n-----BEGIN RSA PRIVATE KEY-----\nFAKEKEYLINE1\nFAKEKEYLINE2\n-----END RSA PRIVATE KEY-----\nafter";
        var cut = "before\n-----BEGIN PRIVATE KEY-----\nFAKEKEYLINE1\nFAKEKEYLINE2";

        Assert.Equal($"before\n-----BEGIN RSA PRIVATE KEY-----\n{R}\n-----END RSA PRIVATE KEY-----\nafter", SecretRedactor.Redact(whole));
        Assert.Equal($"before\n-----BEGIN PRIVATE KEY-----\n{R}\n", SecretRedactor.Redact(cut));
    }

    [Theory]
    // Ordinary deploy output that mentions secrets without containing any.
    [InlineData("Release \"api\" has been upgraded. Happy Helming!\nSTATUS: deployed\nREVISION: 42")]
    [InlineData("Error: could not acquire token: AADSTS7000222 The provided client secret keys are expired")]
    [InlineData("Bearer token expired; refreshing")]
    [InlineData("      mpt-export-config  ConfigMap  Optional: false\n    Environment:  <none>")]
    [InlineData("  secretName: mpt-export-tls\n  tokens: 1234\n  passwordPolicy:\n    Type:  Opaque")]
    [InlineData("SharedAccessKeyName=RootManageSharedAccessKey")]
    [InlineData("https://dev.azure.com/org/project/_build/results?buildId=300&view=logs")]
    [InlineData("if token == null then")]
    [InlineData("")]
    public void TextWithoutSecrets_IsLeftExactlyAsItWas(string input) =>
        Assert.Equal(input, SecretRedactor.Redact(input));

    [Fact]
    public void Redaction_IsIdempotent()
    {
        var input = string.Join("\n",
            "      PYRA_ServiceBus__ConnectionString:  Endpoint=sb://x/;SharedAccessKeyName=a;SharedAccessKey=FAKE=",
            "Authorization: Bearer FAKEbearerTOKEN0123456789",
            "amqps://u:FAKE@h/",
            "password=\"FAKE\"",
            "{\"name\":\"DB_PASSWORD\",\"value\":\"FAKE\"}",
            "-----BEGIN PRIVATE KEY-----\nFAKE\n-----END PRIVATE KEY-----");

        var once = SecretRedactor.Redact(input);

        Assert.DoesNotContain("FAKE", once.Replace("-----BEGIN PRIVATE KEY-----", "").Replace("-----END PRIVATE KEY-----", ""));
        Assert.Equal(once, SecretRedactor.Redact(once));
    }

    [Fact]
    public void LargeInputWithoutBreaks_IsHandledInLinearTime()
    {
        // A long unbroken run (a base64 blob, a minified bundle) must not make any pattern backtrack
        // quadratically — on 512 KiB that would hit the timeout and withhold the whole log.
        var blob = new string('a', 512 * 1024) + " password=FAKE";

        var redacted = SecretRedactor.Redact(blob);

        Assert.EndsWith($" password={R}", redacted);
    }
}
