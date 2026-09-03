using System;
using System.Speech.Recognition;

namespace H145FlightPlanner.Speech
{
    public class SpeechService
    {
        private readonly SpeechRecognitionEngine _recognizer;

        public event EventHandler<string>? SpeechRecognized;
        public event EventHandler<string>? SpeechError;

        public SpeechService()
        {
            try
            {
                _recognizer = new SpeechRecognitionEngine();

                // Use the Windows microphone selected as the default input device.
                _recognizer.SetInputToDefaultAudioDevice();

                // Allow normal free-form speech.
                _recognizer.LoadGrammar(new DictationGrammar());

                _recognizer.SpeechRecognized += OnSpeechRecognized;
                _recognizer.SpeechRecognitionRejected += OnSpeechRecognitionRejected;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "The Windows speech recognition system could not be started.",
                    ex);
            }
        }

        public void StartListening()
        {
            try
            {
                _recognizer.RecognizeAsyncCancel();
                _recognizer.RecognizeAsync(RecognizeMode.Multiple);
            }
            catch (Exception ex)
            {
                SpeechError?.Invoke(this, ex.Message);
            }
        }

        public void StopListening()
        {
            try
            {
                _recognizer.RecognizeAsyncCancel();
            }
            catch (Exception ex)
            {
                SpeechError?.Invoke(this, ex.Message);
            }
        }

        private void OnSpeechRecognized(
            object? sender,
            SpeechRecognizedEventArgs e)
        {
            if (e.Result == null || string.IsNullOrWhiteSpace(e.Result.Text))
                return;

            string recognisedText = e.Result.Text.Trim();

            SpeechRecognized?.Invoke(this, recognisedText);
        }

        private void OnSpeechRecognitionRejected(
            object? sender,
            SpeechRecognitionRejectedEventArgs e)
        {
            // Ignore speech that the recognizer cannot understand.
        }
    }
}
