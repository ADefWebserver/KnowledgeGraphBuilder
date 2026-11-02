using System.Net;
using System.Text.Json;
using KnowledgeGraphBuilder.Shared.Knowledge;
using KnowledgeGraphBuilder.Shared.Knowledge.Persistence;
using KnowledgeGraphBuilder.Shared.Llm;
using KnowledgeGraphBuilder.Shared.Settings;
using RichardSzalay.MockHttp;

namespace KnowledgeGraphBuilder.Tests;

public class SettingsTests
{
    readonly InMemoryKeyValueStore _kv = new();
    readonly TestHost _host = new() { IsBrowser = true };

    BrowserSettingsStore Store() => new(_kv);

    AppSettingsService Service(HttpClient? http = null) => new(Store(), _host, http ?? new HttpClient());

    [Fact]
    public async Task Load_ReturnsDefaults_WhenNothingIsStored()
    {
        var s = await Store().LoadAsync();
        Assert.Equal(StoredSettings.Default, s);
        Assert.Equal(LlmProvider.Anthropic, s.ProviderValue);
        Assert.Equal("claude-sonnet-5-5", s.Model);
        Assert.Null(s.ApiKey);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("""{"provider":""}""")]
    public async Task Load_ReturnsDefaults_WhenStoredValueIsUnusable(string json)
    {
        await _kv.SetAsync(KnowledgeStorageConstants.SettingsKey, json);
        Assert.Equal(StoredSettings.Default, await Store().LoadAsync());
    }

    [Fact]
    public async Task Save_And_Load_RoundTrip_InOneKey()
    {
        var settings = new StoredSettings("Ollama", "llama3.2", "http://localhost:11434/", null);
        await Store().SaveAsync(settings);

        Assert.Equal([KnowledgeStorageConstants.SettingsKey], _kv.Items.Keys);
        Assert.Equal(settings, await Store().LoadAsync());
        var json = JsonDocument.Parse(_kv.Items[KnowledgeStorageConstants.SettingsKey]).RootElement;
        Assert.Equal("Ollama", json.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task ClearApiKey_KeepsTheOtherFields()
    {
        await Store().SaveAsync(new StoredSettings("OpenAI", "gpt-4.1", null, "sk-test"));
        await Store().ClearApiKeyAsync();
        Assert.Equal(new StoredSettings("OpenAI", "gpt-4.1", null, null), await Store().LoadAsync());
    }

    [Fact]
    public async Task Save_QuotaError_Propagates()
    {
        _kv.QuotaBytes = 10;
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => Store().SaveAsync(StoredSettings.Default with { ApiKey = "sk-test" }));
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => Service().SaveAsync(LlmProvider.Anthropic, "m", null, "sk-test"));
    }

    [Fact]
    public async Task Service_SaveKeepsTheKey_WhenNoNewKeyIsGiven()
    {
        var service = Service();
        var changed = 0;
        service.Changed += () => changed++;

        await service.SaveAsync(LlmProvider.Anthropic, " claude-x ", null, " sk-ant-1 ");
        var s = await service.GetAsync();
        Assert.True(s.ApiKeyConfigured);
        Assert.Equal("claude-x", s.Model);
        Assert.Equal("Test root", s.RootDisplay);

        await service.SaveAsync(LlmProvider.Anthropic, "claude-y", null, null);
        Assert.Equal("sk-ant-1", (await Store().LoadAsync()).ApiKey);
        Assert.Equal("claude-y", (await service.GetAsync()).Model);

        await service.ClearApiKeyAsync();
        Assert.False((await service.GetAsync()).ApiKeyConfigured);
        Assert.Null((await Store().LoadAsync()).ApiKey);
        Assert.Equal(3, changed);
    }

    [Fact]
    public async Task Service_Validates()
    {
        var service = Service();
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(LlmProvider.Anthropic, " ", null, null));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(LlmProvider.Ollama, "llama", null, null));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(LlmProvider.AzureOpenAI, "dep", "not a url", "k"));

        // The endpoint is only kept for providers that use it.
        await service.SaveAsync(LlmProvider.OpenAI, "gpt-4.1", "http://ignored/", "k");
        Assert.Null((await Store().LoadAsync()).Endpoint);
    }

    [Fact]
    public async Task Reload_PicksUpAnExternalChange_AndRaisesChanged()
    {
        var service = Service();
        Assert.False((await service.GetAsync()).ApiKeyConfigured);
        var changed = 0;
        service.Changed += () => changed++;

        // Another tab writes kgb:v1:settings.
        await Store().SaveAsync(new StoredSettings("OpenAI", "gpt-4.1", null, "sk-other-tab"));
        await service.ReloadAsync();

        Assert.Equal(1, changed);
        var s = await service.GetAsync();
        Assert.Equal(LlmProvider.OpenAI, s.Provider);
        Assert.True(s.ApiKeyConfigured);
    }

    [Fact]
    public async Task GetClient_WithoutAKey_ReturnsTheReason()
    {
        var result = await Service().GetClientAsync();
        Assert.False(result.Ok);
        Assert.Equal("Add an API key in Settings", result.Reason);
    }

    [Fact]
    public async Task GetClient_WithAKey_IsCached_UntilSettingsChange()
    {
        var service = Service();
        await service.SaveAsync(LlmProvider.Anthropic, "claude-x", null, "sk-ant-1");

        var first = await service.GetClientAsync();
        Assert.True(first.Ok);
        Assert.IsType<AnthropicChatClient>(first.Client);
        Assert.Same(first.Client, (await service.GetClientAsync()).Client);

        await service.SaveAsync(LlmProvider.Anthropic, "claude-y", null, null);
        Assert.NotSame(first.Client, (await service.GetClientAsync()).Client);
    }

    [Fact]
    public async Task TestConnection_ReportsSuccessAndRejectedKeys()
    {
        var mock = new MockHttpMessageHandler();
        var service = Service(mock.ToHttpClient());
        await service.SaveAsync(LlmProvider.Anthropic, "claude-x", null, "sk-ant-secret-123456789");

        mock.When(HttpMethod.Post, AnthropicChatClient.Endpoint).Respond("text/event-stream",
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        Assert.Null(await service.TestConnectionAsync(default));

        mock.Clear();
        mock.When(HttpMethod.Post, AnthropicChatClient.Endpoint)
            .Respond(HttpStatusCode.Unauthorized, "application/json", """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");
        var error = await service.TestConnectionAsync(default);
        Assert.NotNull(error);
        Assert.Contains("401", error);
        Assert.DoesNotContain("sk-ant-secret", error);
    }

    [Fact]
    public void DescribeError_NeverLeaksAKey()
    {
        var message = AppSettingsService.DescribeError(new InvalidOperationException("Failed with key sk-ant-api03-abcdefghijkl"), "api.anthropic.com", false);
        Assert.DoesNotContain("abcdefghijkl", message);
        Assert.Contains("[redacted]", message);

        Assert.Contains("blocked", AppSettingsService.DescribeError(new HttpRequestException("TypeError: Failed to fetch"), "api.anthropic.com", true));
        Assert.Contains("403", AppSettingsService.DescribeError(new HttpRequestException("x", null, HttpStatusCode.Forbidden), null, false));
        Assert.Equal("The provider returned an unexpected response.", AppSettingsService.DescribeError(new JsonException("bad"), null, false));
    }

    [Fact]
    public void StoredSettings_UnknownProvider_FallsBackToTheDefault()
    {
        Assert.Equal(LlmProvider.Anthropic, new StoredSettings("Nope", "m", null, null).ProviderValue);
        Assert.Equal(LlmProvider.OpenAI, new StoredSettings("openai", "m", null, null).ProviderValue);
    }
}
