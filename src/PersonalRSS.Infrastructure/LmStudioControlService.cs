using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace PersonalRSS.Infrastructure;

public sealed record LmStudioModel(string Key, string DisplayName);
public sealed record LmStudioStatus(bool Running, int Port, bool CliAvailable, string? Error, IReadOnlyList<LmStudioModel> Models);

public sealed class LmStudioControlService(IHttpClientFactory httpClientFactory)
{
    private readonly ConcurrentDictionary<OwnedModelInstance, byte> ownedInstances = new();

    public async Task<LmStudioStatus> GetStatusAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (!TryLocalEndpoint(endpoint, out var uri, out var error))
            return new LmStudioStatus(false, 0, true, error, []);
        var command = await RunAsync(["server", "status", "--json", "--quiet"], TimeSpan.FromSeconds(12), cancellationToken);
        var running = false;
        var port = uri!.Port;
        if (command.Started && command.ExitCode == 0)
        {
            try
            {
                using var json = JsonDocument.Parse(command.Output);
                running = json.RootElement.TryGetProperty("running", out var runningValue) && runningValue.GetBoolean();
                if (json.RootElement.TryGetProperty("port", out var portValue)) port = portValue.GetInt32();
            }
            catch (JsonException) { }
        }
        IReadOnlyList<LmStudioModel> models = running ? await GetModelsAsync(uri, cancellationToken) : [];
        var commandError = command.Started ? command.Error : "The lms command was not found.";
        return new LmStudioStatus(running, port, command.Started, string.IsNullOrWhiteSpace(commandError) ? null : commandError.Trim(), models);
    }

    public async Task<LmStudioStatus> StartAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (!TryLocalEndpoint(endpoint, out var uri, out var error))
            return new LmStudioStatus(false, 0, true, error, []);
        var result = await RunAsync(["server", "start", "--port", uri!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--bind", "127.0.0.1"], TimeSpan.FromSeconds(60), cancellationToken);
        if (!result.Started) return new LmStudioStatus(false, uri.Port, false, result.Error, []);
        var status = await GetStatusAsync(endpoint, cancellationToken);
        return status.Running ? status : status with { Error = FirstUseful(result.Error, result.Output, status.Error, "LM Studio did not report a running server.") };
    }

    public async Task<LmStudioStatus> StopAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (!TryLocalEndpoint(endpoint, out var uri, out var error))
            return new LmStudioStatus(false, 0, true, error, []);
        var result = await RunAsync(["server", "stop"], TimeSpan.FromSeconds(30), cancellationToken);
        if (!result.Started) return new LmStudioStatus(false, uri!.Port, false, result.Error, []);
        var status = await GetStatusAsync(endpoint, cancellationToken);
        return status.Running ? status with { Error = FirstUseful(result.Error, result.Output, "LM Studio still reports the server as running.") } : status;
    }

    public async Task<string?> LoadModelAsync(string endpoint, string model, CancellationToken cancellationToken = default)
    {
        if (!TryLocalEndpoint(endpoint, out var uri, out var error))
            throw new InvalidOperationException(error);

        var loadUri = new UriBuilder(uri!.Scheme, uri.Host, uri.Port, "/api/v1/models/load").Uri;
        using var request = new HttpRequestMessage(HttpMethod.Post, loadUri)
        {
            Content = JsonContent.Create(new { model })
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var response = await httpClientFactory.CreateClient(nameof(LmStudioControlService)).SendAsync(request, timeout.Token);
        var content = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(content, null, response.StatusCode);

        using var json = JsonDocument.Parse(content);
        var instanceId = json.RootElement.TryGetProperty("instance_id", out var instanceValue)
            ? instanceValue.GetString()
            : null;
        if (!string.IsNullOrWhiteSpace(instanceId))
            ownedInstances.TryAdd(new OwnedModelInstance(uri, model, instanceId), 0);
        return instanceId;
    }

    public async Task<bool> UnloadOwnedModelAsync(string endpoint, string model, CancellationToken cancellationToken = default)
    {
        if (!TryLocalEndpoint(endpoint, out var uri, out _)) return false;

        var instances = ownedInstances.Keys
            .Where(instance => instance.Matches(uri!, model))
            .ToArray();

        try
        {
            foreach (var instance in instances)
            {
                var unloadUri = new UriBuilder(uri!.Scheme, uri.Host, uri.Port, "/api/v1/models/unload").Uri;
                using var request = new HttpRequestMessage(HttpMethod.Post, unloadUri)
                {
                    Content = JsonContent.Create(new { instance_id = instance.InstanceId })
                };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await httpClientFactory.CreateClient(nameof(LmStudioControlService)).SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode) return false;
                ownedInstances.TryRemove(instance, out _);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return true;
    }

    private async Task<IReadOnlyList<LmStudioModel>> GetModelsAsync(Uri chatEndpoint, CancellationToken cancellationToken)
    {
        var modelInfo = await GetModelInfoAsync(chatEndpoint, cancellationToken);
        return modelInfo is null
            ? []
            : modelInfo.Select(model => new LmStudioModel(model.Key, model.DisplayName)).ToArray();
    }

    private async Task<IReadOnlyList<ModelInfo>?> GetModelInfoAsync(Uri chatEndpoint, CancellationToken cancellationToken)
    {
        try
        {
            var modelsUri = new UriBuilder(chatEndpoint.Scheme, chatEndpoint.Host, chatEndpoint.Port, "/api/v1/models").Uri;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await httpClientFactory.CreateClient(nameof(LmStudioControlService)).GetAsync(modelsUri, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (!json.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return [];
            return models.EnumerateArray()
                .Where(model => !model.TryGetProperty("type", out var type) || type.GetString() == "llm")
                .Select(model =>
                {
                    var key = model.TryGetProperty("key", out var keyValue) ? keyValue.GetString() : null;
                    var name = model.TryGetProperty("display_name", out var nameValue) ? nameValue.GetString() : null;
                    return string.IsNullOrWhiteSpace(key) ? null : new ModelInfo(key, string.IsNullOrWhiteSpace(name) ? key : name!);
                })
                .Where(model => model is not null).Cast<ModelInfo>().OrderBy(model => model.DisplayName).ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    public static bool TryLocalEndpoint(string endpoint, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttp ||
            !string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase) && parsed.Host != "127.0.0.1" && parsed.Host != "::1")
        {
            error = "Use a local HTTP endpoint on localhost or 127.0.0.1.";
            return false;
        }
        uri = parsed;
        return true;
    }

    private static async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "lms",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return new CommandResult(false, -1, "", "Could not start lms.");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(true); } catch { }
                return new CommandResult(true, -1, await outputTask, "The lms command timed out.");
            }
            return new CommandResult(true, process.ExitCode, await outputTask, await errorTask);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new CommandResult(false, -1, "", exception.Message);
        }
    }

    private static string? FirstUseful(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    private sealed record ModelInfo(string Key, string DisplayName);
    private sealed record OwnedModelInstance(Uri Endpoint, string Model, string InstanceId)
    {
        public bool Matches(Uri endpoint, string model) =>
            Endpoint.Scheme == endpoint.Scheme &&
            string.Equals(Endpoint.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase) &&
            Endpoint.Port == endpoint.Port &&
            string.Equals(Model, model, StringComparison.Ordinal);
    }
    private sealed record CommandResult(bool Started, int ExitCode, string Output, string Error);
}
