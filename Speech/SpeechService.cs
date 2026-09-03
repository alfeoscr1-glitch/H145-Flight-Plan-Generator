using System;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace H145FlightPlanner.Speech
{
    public class SpeechService
    {
        private SpeechRecognizer? _recognizer;

        public event EventHandler<string>? SpeechRecognized;
        public event EventHandler<string>? SpeechError;

        public async void StartListening()
        {
            try
            {
                string key = SpeechConfiguration.SpeechKey;
                string region = SpeechConfiguration.SpeechRegion;

                if (string.IsNullOrWhiteSpace(key) ||
                    string.IsNullOrWhiteSpace(region))
                {
                    SpeechError?.Invoke(
                        this,
                        "Azure Speech is not configured yet.");
                    return;
                }

                var speechConfig =
                    SpeechConfig.FromSubscription(key, region);

                speechConfig.SpeechRecognitionLanguage = "en-GB";

                using var audioConfig =
                    AudioConfig.FromDefaultMicrophoneInput();

                _recognizer = new SpeechRecognizer(
                    speechConfig,
                    audioConfig);

                _recognizer.Recognized += Recognizer_Recognized;

                await _recognizer.StartContinuousRecognitionAsync();
            }
            catch (Exception ex)
            {
                SpeechError?.Invoke(this, ex.Message);
            }
        }

        public async void StopListening()
        {
            try
            {
                if (_recognizer == null)
                    return;

                await _recognizer.StopContinuousRecognitionAsync();

                _recognizer.Recognized -= Recognizer_Recognized;
                _recognizer.Dispose();
                _recognizer = null;
            }
            catch (Exception ex)
            {
                SpeechError?.Invoke(this, ex.Message);
            }
        }

        private void Recognizer_Recognized(
            object? sender,
            SpeechRecognitionEventArgs e)
        {
            if (e.Result == null)
                return;

            if (e.Result.Reason != ResultReason.RecognizedSpeech)
                return;

            string text = e.Result.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text))
                return;

            SpeechRecognized?.Invoke(this, text);
        }
    }
}
