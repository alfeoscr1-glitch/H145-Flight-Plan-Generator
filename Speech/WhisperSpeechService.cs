using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Whisper.net;

namespace H145FlightPlanner.Speech
{
    public class WhisperSpeechService : IDisposable
    {
        private readonly string _modelPath;
        private WhisperFactory? _factory;
        private WhisperProcessor? _processor;

        public WhisperSpeechService(string modelPath)
        {
            _modelPath = modelPath;

            if (!File.Exists(_modelPath))
            {
                throw new FileNotFoundException(
                    "The Whisper model could not be found.",
                    _modelPath);
            }

            _factory = WhisperFactory.FromPath(_modelPath);

            _processor = _factory
                .CreateBuilder()
                .WithLanguage("en")
                .WithNoContext()
                .Build();
        }

        public async Task<string> TranscribeAsync(
            Stream wavStream,
            CancellationToken cancellationToken = default)
        {
            if (_processor == null)
                throw new ObjectDisposedException(nameof(WhisperSpeechService));

            using var memoryStream = new MemoryStream();

            await wavStream.CopyToAsync(
                memoryStream,
                cancellationToken);

            memoryStream.Position = 0;

            string result = string.Empty;

            await foreach (var segment in _processor.ProcessAsync(
                memoryStream,
                cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    result += segment.Text;
                }
            }

            return result.Trim();
        }

        public static MemoryStream ConvertToWhisperWav(
            IWaveProvider source)
        {
            var output = new MemoryStream();

            using (var writer = new WaveFileWriter(
                output,
                new WaveFormat(16000, 16, 1)))
            {
                byte[] buffer = new byte[4096];

                int bytesRead;

                while ((bytesRead = source.Read(
                    buffer,
                    0,
                    buffer.Length)) > 0)
                {
                    writer.Write(buffer, 0, bytesRead);
                }

                writer.Flush();
            }

            output.Position = 0;
            return output;
        }

        public void Dispose()
        {
            _processor?.Dispose();
            _factory?.Dispose();

            _processor = null;
            _factory = null;
        }
    }
}
