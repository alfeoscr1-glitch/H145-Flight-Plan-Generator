using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace H145FlightPlanner.Services
{
    public class LocalAiModelManager
    {
        private static readonly HttpClient HttpClient =
            CreateHttpClient();

        private readonly string _root;
        private readonly string _runtimeFolder;
        private readonly string _modelFolder;

        public LocalAiModelManager()
        {
            _root = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "H145FlightPlanner",
                "AI");

            _runtimeFolder =
                Path.Combine(
                    _root,
                    "llama-runtime");

            _modelFolder =
                Path.Combine(
                    _root,
                    "Models");
        }

        public async Task<(string RuntimeExe, string ModelPath)>
            EnsureReadyAsync(
                CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(
                _runtimeFolder);

            Directory.CreateDirectory(
                _modelFolder);

            string runtime =
                FindRuntimeExe();

            if (string.IsNullOrWhiteSpace(runtime))
            {
                runtime =
                    await DownloadRuntimeAsync(
                        cancellationToken);
            }

            string model =
                await EnsureModelAsync(
                    cancellationToken);

            return (runtime, model);
        }

        private static HttpClient CreateHttpClient()
        {
            var client =
                new HttpClient();

            client.Timeout =
                TimeSpan.FromMinutes(30);

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "H145FlightPlanGenerator/1.0");

            client.DefaultRequestHeaders.Accept.ParseAdd(
                "application/vnd.github+json");

            return client;
        }

        private string FindRuntimeExe()
        {
            if (!Directory.Exists(
                    _runtimeFolder))
            {
                return string.Empty;
            }

            string[] possibleNames =
            {
                "llama-cli.exe",
                "main.exe"
            };

            foreach (string name in possibleNames)
            {
                string? result =
                    Directory
                        .EnumerateFiles(
                            _runtimeFolder,
                            name,
                            SearchOption.AllDirectories)
                        .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(result))
                    return result;
            }

            return string.Empty;
        }

        private async Task<string> DownloadRuntimeAsync(
            CancellationToken cancellationToken)
        {
            List<(string Name, string Url)> candidates =
                await FindRuntimeCandidatesAsync(
                    cancellationToken);

            (string Name, string Url) selected =
                SelectBestRuntime(candidates);

            if (string.IsNullOrWhiteSpace(
                    selected.Url))
            {
                string available =
                    candidates.Count == 0
                        ? "No matching Windows x64 ZIP assets were returned."
                        : string.Join(
                            Environment.NewLine,
                            candidates.Select(
                                x => x.Name));

                throw new InvalidOperationException(
                    "A Windows x64 local AI runtime could not be found." +
                    Environment.NewLine +
                    Environment.NewLine +
                    "Matching assets seen:" +
                    Environment.NewLine +
                    available);
            }

            string zipPath =
                Path.Combine(
                    _root,
                    "llama-runtime.zip");

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            using (
                HttpResponseMessage download =
                    await HttpClient.GetAsync(
                        selected.Url,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken))
            {
                download.EnsureSuccessStatusCode();

                await using Stream source =
                    await download.Content.ReadAsStreamAsync(
                        cancellationToken);

                await using FileStream target =
                    File.Create(zipPath);

                await source.CopyToAsync(
                    target,
                    cancellationToken);
            }

            if (Directory.Exists(
                    _runtimeFolder))
            {
                Directory.Delete(
                    _runtimeFolder,
                    true);
            }

            Directory.CreateDirectory(
                _runtimeFolder);

            ZipFile.ExtractToDirectory(
                zipPath,
                _runtimeFolder,
                true);

            File.Delete(
                zipPath);

            string exe =
                FindRuntimeExe();

            if (string.IsNullOrWhiteSpace(exe))
            {
                throw new InvalidOperationException(
                    "The local AI runtime downloaded successfully, " +
                    "but llama-cli.exe was not found inside it.");
            }

            return exe;
        }

        private async Task<
            List<(string Name, string Url)>>
            FindRuntimeCandidatesAsync(
                CancellationToken cancellationToken)
        {
            // -------------------------------------------------
            // llama.cpp changed the way its GitHub releases
            // are presented.
            //
            // /releases/latest may now contain a small
            // nightly-tag.txt pointer instead of the actual
            // Windows binaries.
            //
            // First try to resolve that pointer to the real
            // bXXXXX build release.
            // -------------------------------------------------

            const string latestReleaseUrl =
                "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest";

            JsonDocument latestDocument =
                await DownloadJsonAsync(
                    latestReleaseUrl,
                    cancellationToken);

            using (latestDocument)
            {
                List<(string Name, string Url)> directCandidates =
                    ExtractWindowsRuntimeCandidates(
                        latestDocument.RootElement);

                // If GitHub changes back to putting binaries
                // directly on "latest", this still works.
                if (directCandidates.Count > 0)
                {
                    return directCandidates;
                }

                string nightlyTagUrl =
                    FindAssetUrl(
                        latestDocument.RootElement,
                        "nightly-tag.txt");

                if (!string.IsNullOrWhiteSpace(
                        nightlyTagUrl))
                {
                    string nightlyTag =
                        await DownloadTextAsync(
                            nightlyTagUrl,
                            cancellationToken);

                    nightlyTag =
                        nightlyTag.Trim();

                    Match tagMatch =
                        Regex.Match(
                            nightlyTag,
                            @"b\d+",
                            RegexOptions.IgnoreCase);

                    if (tagMatch.Success)
                    {
                        string buildTag =
                            tagMatch.Value;

                        List<(string Name, string Url)> tagCandidates =
                            await GetCandidatesForTagAsync(
                                buildTag,
                                cancellationToken);

                        if (tagCandidates.Count > 0)
                        {
                            return tagCandidates;
                        }
                    }
                }
            }

            // -------------------------------------------------
            // FALLBACK
            //
            // If nightly-tag.txt was unavailable, inspect recent
            // releases and take the newest one that actually
            // contains a Windows x64 llama.cpp runtime.
            // -------------------------------------------------

            for (int page = 1;
                 page <= 5;
                 page++)
            {
                string releasesUrl =
                    "https://api.github.com/repos/ggml-org/llama.cpp/releases" +
                    $"?per_page=20&page={page}";

                JsonDocument releasesDocument =
                    await DownloadJsonAsync(
                        releasesUrl,
                        cancellationToken);

                using (releasesDocument)
                {
                    if (releasesDocument.RootElement.ValueKind !=
                        JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (
                        JsonElement release
                        in releasesDocument.RootElement.EnumerateArray())
                    {
                        List<(string Name, string Url)> candidates =
                            ExtractWindowsRuntimeCandidates(
                                release);

                        if (candidates.Count > 0)
                        {
                            return candidates;
                        }
                    }
                }
            }

            return new List<(string Name, string Url)>();
        }

        private async Task<
            List<(string Name, string Url)>>
            GetCandidatesForTagAsync(
                string tag,
                CancellationToken cancellationToken)
        {
            string encodedTag =
                Uri.EscapeDataString(
                    tag);

            string releaseUrl =
                "https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/" +
                encodedTag;

            try
            {
                JsonDocument document =
                    await DownloadJsonAsync(
                        releaseUrl,
                        cancellationToken);

                using (document)
                {
                    return ExtractWindowsRuntimeCandidates(
                        document.RootElement);
                }
            }
            catch (HttpRequestException)
            {
                return new List<(string Name, string Url)>();
            }
        }

        private static List<(string Name, string Url)>
            ExtractWindowsRuntimeCandidates(
                JsonElement release)
        {
            var candidates =
                new List<(string Name, string Url)>();

            if (!release.TryGetProperty(
                    "assets",
                    out JsonElement assets) ||
                assets.ValueKind != JsonValueKind.Array)
            {
                return candidates;
            }

            foreach (
                JsonElement asset
                in assets.EnumerateArray())
            {
                string name =
                    asset.TryGetProperty(
                        "name",
                        out JsonElement nameElement)
                        ? nameElement.GetString()
                          ?? string.Empty
                        : string.Empty;

                string url =
                    asset.TryGetProperty(
                        "browser_download_url",
                        out JsonElement urlElement)
                        ? urlElement.GetString()
                          ?? string.Empty
                        : string.Empty;

                if (string.IsNullOrWhiteSpace(name) ||
                    string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                bool isZip =
                    name.EndsWith(
                        ".zip",
                        StringComparison.OrdinalIgnoreCase);

                bool isWindows =
                    name.Contains(
                        "win",
                        StringComparison.OrdinalIgnoreCase);

                bool isX64 =
                    name.Contains(
                        "x64",
                        StringComparison.OrdinalIgnoreCase);

                bool isLlama =
                    name.Contains(
                        "llama",
                        StringComparison.OrdinalIgnoreCase);

                bool isRuntimeOnly =
                    name.StartsWith(
                        "cudart-",
                        StringComparison.OrdinalIgnoreCase);

                if (isZip &&
                    isWindows &&
                    isX64 &&
                    isLlama &&
                    !isRuntimeOnly)
                {
                    candidates.Add(
                        (name, url));
                }
            }

            return candidates;
        }

        private static (
            string Name,
            string Url)
            SelectBestRuntime(
                List<(string Name, string Url)> candidates)
        {
            // -------------------------------------------------
            // FIRST CHOICE:
            // CPU x64 build.
            //
            // This avoids requiring CUDA/Vulkan/etc. and should
            // run on a normal Windows x64 machine.
            // -------------------------------------------------

            (string Name, string Url) selected =
                candidates.FirstOrDefault(
                    x =>
                        x.Name.Contains(
                            "cpu",
                            StringComparison.OrdinalIgnoreCase) &&
                        !ContainsGpuBackend(
                            x.Name));

            // -------------------------------------------------
            // SECOND CHOICE:
            // Generic Windows x64 build with no GPU backend
            // explicitly attached to the package name.
            // -------------------------------------------------

            if (string.IsNullOrWhiteSpace(
                    selected.Url))
            {
                selected =
                    candidates.FirstOrDefault(
                        x =>
                            !ContainsGpuBackend(
                                x.Name));
            }

            // -------------------------------------------------
            // THIRD CHOICE:
            // Vulkan.
            // -------------------------------------------------

            if (string.IsNullOrWhiteSpace(
                    selected.Url))
            {
                selected =
                    candidates.FirstOrDefault(
                        x =>
                            x.Name.Contains(
                                "vulkan",
                                StringComparison.OrdinalIgnoreCase));
            }

            // -------------------------------------------------
            // LAST RESORT:
            // Any Windows x64 llama binary archive.
            // -------------------------------------------------

            if (string.IsNullOrWhiteSpace(
                    selected.Url))
            {
                selected =
                    candidates.FirstOrDefault();
            }

            return selected;
        }

        private static string FindAssetUrl(
            JsonElement release,
            string assetName)
        {
            if (!release.TryGetProperty(
                    "assets",
                    out JsonElement assets) ||
                assets.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            foreach (
                JsonElement asset
                in assets.EnumerateArray())
            {
                string name =
                    asset.TryGetProperty(
                        "name",
                        out JsonElement nameElement)
                        ? nameElement.GetString()
                          ?? string.Empty
                        : string.Empty;

                if (!string.Equals(
                        name,
                        assetName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return asset.TryGetProperty(
                    "browser_download_url",
                    out JsonElement urlElement)
                        ? urlElement.GetString()
                          ?? string.Empty
                        : string.Empty;
            }

            return string.Empty;
        }

        private static async Task<JsonDocument>
            DownloadJsonAsync(
                string url,
                CancellationToken cancellationToken)
        {
            using HttpResponseMessage response =
                await HttpClient.GetAsync(
                    url,
                    cancellationToken);

            response.EnsureSuccessStatusCode();

            string json =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            return JsonDocument.Parse(
                json);
        }

        private static async Task<string>
            DownloadTextAsync(
                string url,
                CancellationToken cancellationToken)
        {
            using HttpResponseMessage response =
                await HttpClient.GetAsync(
                    url,
                    cancellationToken);

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStringAsync(
                cancellationToken);
        }

        private static bool ContainsGpuBackend(
            string name)
        {
            string[] gpuWords =
            {
                "cuda",
                "vulkan",
                "sycl",
                "openvino",
                "hip",
                "rocm",
                "radeon"
            };

            return gpuWords.Any(
                word =>
                    name.Contains(
                        word,
                        StringComparison.OrdinalIgnoreCase));
        }

        private async Task<string> EnsureModelAsync(
            CancellationToken cancellationToken)
        {
            string modelPath =
                Path.Combine(
                    _modelFolder,
                    "Qwen2.5-3B-Instruct-Q4_K_M.gguf");

            if (File.Exists(modelPath) &&
                new FileInfo(modelPath).Length >
                500_000_000)
            {
                return modelPath;
            }

            string[] urls =
            {
                "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf?download=true",
                "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_S.gguf?download=true"
            };

            Exception? lastError =
                null;

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

                    string tempPath =
                        modelPath +
                        ".download";

                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }

                    await using Stream source =
                        await response.Content.ReadAsStreamAsync(
                            cancellationToken);

                    await using (
                        FileStream target =
                            File.Create(tempPath))
                    {
                        await source.CopyToAsync(
                            target,
                            cancellationToken);
                    }

                    File.Move(
                        tempPath,
                        modelPath,
                        true);

                    return modelPath;
                }
                catch (Exception ex)
                {
                    lastError =
                        ex;
                }
            }

            throw new InvalidOperationException(
                "The free local route-understanding model " +
                "could not be downloaded.",
                lastError);
        }
    }
}
