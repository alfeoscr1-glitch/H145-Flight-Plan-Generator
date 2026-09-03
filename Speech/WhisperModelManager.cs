using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;

namespace H145FlightPlanner.Speech
{
    public static class WhisperModelManager
    {
        private const string ModelFileName = "ggml-large-v3-turbo.bin";

        public static string GetModelPath()
        {
            string modelDirectory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "H145FlightPlanner",
                "Models");

            Directory.CreateDirectory(modelDirectory);

            return Path.Combine(modelDirectory, ModelFileName);
        }

        public static bool ModelExists()
        {
            return File.Exists(GetModelPath());
        }

        public static async Task<string> EnsureModelExistsAsync(
            CancellationToken cancellationToken = default)
        {
            string modelPath = GetModelPath();

            if (File.Exists(modelPath))
                return modelPath;

            using Stream modelStream =
                await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                    GgmlType.LargeV3Turbo);

            using FileStream fileStream = new FileStream(
                modelPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);

            await modelStream.CopyToAsync(
                fileStream,
                cancellationToken);

            return modelPath;
        }
    }
}
