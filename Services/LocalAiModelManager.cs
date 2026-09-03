using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace H145FlightPlanner.Services
{
    public class LocalAiModelManager
    {
        private static readonly HttpClient HttpClient = CreateHttpClient();

        private readonly string _root;
        private readonly string _runtimeFolder;
        private readonly string _modelFolder;

        public LocalAiModelManager()
        {
            _root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "H145FlightPlanner",
                "AI");

            _runtimeFolder = Path.Combine(_root, "llama-runtime");
            _modelFolder = Path.Combine(_root, "Models");
        }

        public async Task<(string RuntimeExe, string ModelPath)> EnsureReadyAsync(
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(_runtimeFolder);
            Directory.CreateDirectory(_modelFolder);

            string runtime = FindRuntimeExe();
            if (string.IsNullOrWhiteSpace(runtime))
            {
                runtime = await DownloadRuntimeAsync(cancellationToken);
            }

            string model = await EnsureModelAsync(cancellationToken);
            return (runtime, model);
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("H145FlightPlanGenerator/1.0");
            return client;
        }

        private string FindRuntimeExe()
        {
            if (!Directory.Exists(_runtimeFolder))
                return string.Empty;

            return Directory
                .EnumerateFiles(_runtimeFolder, "llama-cli.exe", SearchOption.AllDirectories)
                .FirstOrDefault() ?? string.Empty;
        }

        private async Task<string> DownloadRuntimeAsync(
            CancellationToken cancellationToken)
        {
            const string releasesUrl =
                "https://api.github.com/repos/ggerganov/llama.cpp/releases/latest";

            using HttpResponseMessage response =
                await HttpClient.GetAsync(releasesUrl, cancellationToken);

            response.EnsureSuccessStatusCode();

            string json =
                await response.Content.ReadAsStringAsync(cancellationToken);

            using JsonDocument document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("assets", out JsonElement assets) ||
                assets.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "The local AI runtime release could not be discovered.");
            }

            var candidates = new List<(string Name, string Url)>();

            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name =
                    asset.TryGetProperty("name", out JsonElement nameElement)
                        ? nameElement.GetString() ?? string.Empty
                        : string.Empty;

                string url =
                    asset.TryGetProperty("browser_download_url", out JsonElement urlElement)
                        ? urlElement.GetString() ?? string.Empty
                        : string.Empty;

                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    name.Contains("win", StringComparison.OrdinalIgnoreCase) &&
                    name.Contains("x64", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add((name, url));
                }
            }

            (string Name, string Url) selected =
                candidates.FirstOrDefault(x =>
                    x.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(selected.Url))
            {
                selected = candidates.FirstOrDefault(x =>
                    !x.Name.Contains("cuda", StringComparison.OrdinalIgnoreCase) &&
                    !x.Name.Contains("vulkan", StringComparison.OrdinalIgnoreCase));
            }

            if (string.IsNullOrWhiteSpace(selected.Url))
            {
                throw new InvalidOperationException(
                    "A Windows x64 local AI runtime could not be found in the latest llama.cpp release.");
            }

            string zipPath = Path.Combine(_root, "llama-runtime.zip");

            using (HttpResponseMessage download =
                await HttpClient.GetAsync(
                    selected.Url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken))
            {
                download.EnsureSuccessStatusCode();

                await using Stream source =
                    await download.Content.ReadAsStreamAsync(cancellationToken);

                await using FileStream target =
                    File.Create(zipPath);

                await source.CopyToAsync(target, cancellationToken);
            }

            if (Directory.Exists(_runtimeFolder))
            {
                Directory.Delete(_runtimeFolder, true);
            }

            Directory.CreateDirectory(_runtimeFolder);
            ZipFile.ExtractToDirectory(zipPath, _runtimeFolder, true);
            File.Delete(zipPath);

            string exe = FindRuntimeExe();

            if (string.IsNullOrWhiteSpace(exe))
            {
                throw new InvalidOperationException(
                    "The local AI runtime downloaded, but llama-cli.exe was not found inside it.");
            }

            return exe;
        }

        private async Task<string> EnsureModelAsync(
            CancellationToken cancellationToken)
        {
            string modelPath =
                Path.Combine(_modelFolder, "Qwen2.5-3B-Instruct-Q4_K_M.gguf");

            if (File.Exists(modelPath) && new FileInfo(modelPath).Length > 500_000_000)
                return modelPath;

            string[] urls =
            {
                "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf?download=true",
                "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_S.gguf?download=true"
            };

            Exception? lastError = null;

            foreach (string url in urls)
            {
                try
                {
                    using HttpResponseMessage response =
                        await HttpClient.GetAsync(
                            url,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken);

                    response.EnsureSuccessStatusCode();

                    string tempPath = modelPath + ".download";

                    await using Stream source =
                        await response.Content.ReadAsStreamAsync(cancellationToken);

                    await using (FileStream target = File.Create(tempPath))
                    {
                        await source.CopyToAsync(target, cancellationToken);
                    }

                    File.Move(tempPath, modelPath, true);
                    return modelPath;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            throw new InvalidOperationException(
                "The free local route-understanding model could not be downloaded.",
                lastError);
        }
    }
}
