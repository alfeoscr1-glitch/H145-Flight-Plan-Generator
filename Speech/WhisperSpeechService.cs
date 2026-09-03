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
        private WhisperFactory? _factory;
        private WhisperProcessor? _processor;

        private WaveInEvent? _waveIn;
        private MemoryStream? _recordingStream;
        private WaveFileWriter? _waveWriter;

        private bool _isRecording;

        public event EventHandler<string>? TranscriptionReceived;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? SpeechError;

        public async Task InitializeAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                StatusChanged?.Invoke(this, "Loading Whisper model...");

                string modelPath =
                    await WhisperModelManager.EnsureModelExistsAsync(
                        cancellationToken);

                _factory = WhisperFactory.FromPath(modelPath);

                _processor = _factory
                    .CreateBuilder()
                    .WithLanguage("en")
                    .Build();

                StatusChanged?.Invoke(this, "Whisper ready");
            }
            catch (Exception ex)
            {
                SpeechError?.Invoke(
                    this,
                    $"Whisper could not be initialized: {ex.Message}");
            }
        }

        public void StartListening()
        {
            if (_processor == null)
            {
                SpeechError?.Invoke(
                    this,
                    "Whisper is not ready yet.");
                return;
            }

            if (_isRecording)
                return;

            try
            {
                _recordingStream = new MemoryStream();

                _waveWriter = new WaveFileWriter(
                    _recordingStream,
                    new WaveFormat(16000, 16, 1));

                _waveIn = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 100
                };

                _waveIn.DataAvailable += WaveIn_DataAvailable;
                _waveIn.RecordingStopped += WaveIn_RecordingStopped;

                _isRecording = true;

                _waveIn.StartRecording();

                StatusChanged?.Invoke(this, "Listening with Whisper");
            }
            catch (Exception ex)
            {
                CleanupRecording();

                SpeechError?.Invoke(
                    this,
                    $"Microphone could not be started: {ex.Message}");
            }
        }

        public void StopListening()
        {
            if (!_isRecording || _waveIn == null)
                return;

            try
            {
                StatusChanged?.Invoke(this, "Processing with Whisper...");

                _waveIn.StopRecording();
            }
            catch (Exception ex)
            {
                CleanupRecording();

                SpeechError?.Invoke(
                    this,
                    $"Microphone could not be stopped: {ex.Message}");
            }
        }

        private void WaveIn_DataAvailable(
            object? sender,
            WaveInEventArgs e)
        {
            if (_waveWriter == null)
                return;

            _waveWriter.Write(
                e.Buffer,
                0,
                e.BytesRecorded);

            _waveWriter.Flush();
        }

        private async void WaveIn_RecordingStopped(
            object? sender,
            StoppedEventArgs e)
        {
            try
            {
                if (e.Exception != null)
                {
                    SpeechError?.Invoke(
                        this,
                        $"Microphone error: {e.Exception.Message}");

                    CleanupRecording();
                    return;
                }

                if (_recordingStream == null ||
                    _waveWriter == null ||
                    _processor == null)
                {
                    CleanupRecording();
                    return;
                }

                _waveWriter.Flush();

                _recordingStream.Position = 0;

                string transcription = string.Empty;

                await foreach (var segment in _processor.ProcessAsync(
                    _recordingStream,
                    CancellationToken.None))
                {
                    if (string.IsNullOrWhiteSpace(segment.Text))
                        continue;

                    transcription += segment.Text;
                }

                transcription = transcription.Trim();

                if (!string.IsNullOrWhiteSpace(transcription))
                {
                    TranscriptionReceived?.Invoke(
                        this,
                        transcription);
                }

                StatusChanged?.Invoke(
                    this,
                    "Ready");
            }
            catch (Exception ex)
            {
                SpeechError?.Invoke(
                    this,
                    $"Whisper transcription failed: {ex.Message}");
            }
            finally
            {
                CleanupRecording();
            }
        }

        private void CleanupRecording()
        {
            _isRecording = false;

            if (_waveIn != null)
            {
                _waveIn.DataAvailable -= WaveIn_DataAvailable;
                _waveIn.RecordingStopped -= WaveIn_RecordingStopped;
                _waveIn.Dispose();
                _waveIn = null;
            }

            _waveWriter?.Dispose();
            _waveWriter = null;

            _recordingStream?.Dispose();
            _recordingStream = null;
        }

        public void Dispose()
        {
            try
            {
                if (_waveIn != null)
                    _waveIn.StopRecording();
            }
            catch
            {
                // Ignore shutdown errors.
            }

            CleanupRecording();

            _processor?.Dispose();
            _processor = null;

            _factory?.Dispose();
            _factory = null;
        }
    }
}
