using System;
using System.Speech.Recognition;

namespace H145FlightPlanner.Speech
{
    public class SpeechService
    {
        private readonly SpeechRecognitionEngine _recognizer;
        private readonly DictationGrammar _dictationGrammar;

        public event EventHandler<string>? SpeechRecognized;
        public event EventHandler<string>? SpeechError;

        public SpeechService()
        {
            try
            {
                _recognizer = new SpeechRecognitionEngine();

                // Use the Windows default microphone.
                _recognizer.SetInputToDefaultAudioDevice();

                // IMPORTANT:
                // Free-form dictation means the speech system is not
                // restricted to a predefined list of commands.
                _dictationGrammar = new DictationGrammar
                {
                    Name = "H145 Free Speech"
                };

                _recognizer.LoadGrammar(_dictationGrammar);

                _recognizer.SpeechRecognized += OnSpeechRecognized;
                _recognizer.SpeechRecognitionRejected += OnSpeechRejected;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Windows speech recognition could not be started. " +
                    "Please make sure a working microphone is available.",
                    ex);
            }
        }

        public void StartListening()
        {
            try
            {
                _recognizer.RecognizeAsyncCancel();

                _recognizer.RecognizeAsync(
                    RecognizeMode.Multiple);
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
            if (e.Result == null)
                return;

            // Ignore extremely low-confidence recognition.
            if (e.Result.Confidence < 0.35f)
                return;

            string text = e.Result.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text))
                return;

            // Improve known aviation identifiers without restricting
            // what the user is allowed to say.
            text = NormalizeSpeech(text);

            SpeechRecognized?.Invoke(this, text);
        }

        private void OnSpeechRejected(
            object? sender,
            SpeechRecognitionRejectedEventArgs e)
        {
            // We deliberately do nothing here.
            //
            // The recognizer is free-form dictation and the application
            // should never reject a sentence simply because it doesn't
            // match one of our flight-plan commands.
        }

        private string NormalizeSpeech(string text)
        {
            text = text.Trim();

            // ICAO codes commonly spoken using individual letters.
            text = ReplaceIgnoreCase(text, "E G C K", "EGCK");
            text = ReplaceIgnoreCase(text, "E G F A", "EGFA");
            text = ReplaceIgnoreCase(text, "E G P H", "EGPH");
            text = ReplaceIgnoreCase(text, "E G F F", "EGFF");
            text = ReplaceIgnoreCase(text, "E G N T", "EGNT");
            text = ReplaceIgnoreCase(text, "E G P B", "EGPB");

            // ICAO codes spoken using the NATO phonetic alphabet.
            text = ReplaceIgnoreCase(
                text,
                "Echo Golf Charlie Kilo",
                "EGCK");

            text = ReplaceIgnoreCase(
                text,
                "Echo Golf Foxtrot Alpha",
                "EGFA");

            text = ReplaceIgnoreCase(
                text,
                "Echo Golf Papa Hotel",
                "EGPH");

            text = ReplaceIgnoreCase(
                text,
                "Echo Golf Foxtrot Foxtrot",
                "EGFF");

            text = ReplaceIgnoreCase(
                text,
                "Echo Golf November Tango",
                "EGNT");

            text = ReplaceIgnoreCase(
                text,
                "Echo Golf Papa Bravo",
                "EGPB");

            return text;
        }

        private static string ReplaceIgnoreCase(
            string source,
            string oldValue,
            string newValue)
        {
            return source.Replace(
                oldValue,
                newValue,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
