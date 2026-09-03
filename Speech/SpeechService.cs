using System;
using System.Speech.Recognition;

namespace H145FlightPlanner.Speech
{
    public class SpeechService
    {
        private readonly SpeechRecognitionEngine _recognizer;
        private readonly Grammar _dictationGrammar;
        private readonly Grammar _aviationGrammar;

        public event EventHandler<string>? SpeechRecognized;
        public event EventHandler<string>? SpeechError;

        public SpeechService()
        {
            try
            {
                _recognizer = new SpeechRecognitionEngine();

                // Use the Windows default microphone.
                _recognizer.SetInputToDefaultAudioDevice();

                // General free-form speech.
                _dictationGrammar = new DictationGrammar
                {
                    Name = "General Dictation"
                };

                _recognizer.LoadGrammar(_dictationGrammar);

                // Aviation-specific grammar.
                _aviationGrammar = BuildAviationGrammar();

                _recognizer.LoadGrammar(_aviationGrammar);

                _recognizer.SpeechRecognized += Recognizer_SpeechRecognized;
                _recognizer.SpeechRecognitionRejected += Recognizer_SpeechRecognitionRejected;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Windows speech recognition could not be started. " +
                    "Make sure Windows has a microphone available.",
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

        private Grammar BuildAviationGrammar()
        {
            var builder = new GrammarBuilder();

            var commands = new Choices(
                "create a flight plan",
                "get a flight plan",
                "make a flight plan"
            );

            var starting = new Choices(
                "starting from",
                "start from",
                "starting at",
                "start at",
                "departing from",
                "departure from"
            );

            var ending = new Choices(
                "and ending at",
                "ending at",
                "end at",
                "and end at",
                "arriving at",
                "destination"
            );

            // Common ways of saying EGCK.
            var egck = new Choices(
                "EGCK",
                "E G C K",
                "E G C K airport",
                "Echo Golf Charlie Kilo",
                "Echo Golf Charlie Kilo airport"
            );

            // Common ways of saying EGFA.
            var egfa = new Choices(
                "EGFA",
                "E G F A",
                "E G F A airport",
                "Echo Golf Foxtrot Alpha",
                "Echo Golf Foxtrot Alpha airport"
            );

            var egckOrEgfa = new Choices();
            egckOrEgfa.Add(egck);
            egckOrEgfa.Add(egfa);

            builder.Append(commands);
            builder.Append(starting);
            builder.Append(egckOrEgfa);
            builder.Append(ending);
            builder.Append(egckOrEgfa);

            return new Grammar(builder)
            {
                Name = "H145 Aviation Commands"
            };
        }

        private void Recognizer_SpeechRecognized(
            object? sender,
            SpeechRecognizedEventArgs e)
        {
            if (e.Result == null)
                return;

            if (e.Result.Confidence < 0.55f)
                return;

            string text = e.Result.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text))
                return;

            // Normalize known aviation identifiers.
            text = NormalizeAviationText(text);

            SpeechRecognized?.Invoke(this, text);
        }

        private void Recognizer_SpeechRecognitionRejected(
            object? sender,
            SpeechRecognitionRejectedEventArgs e)
        {
            // Ignore speech that Windows cannot recognize confidently.
        }

        private string NormalizeAviationText(string text)
        {
            text = text.Trim();

            text = ReplaceIgnoreCase(
                text,
                "E G C K",
                "EGCK");

            text = ReplaceIgnoreCase(
                text,
                "E G F A",
                "EGFA");

            text = ReplaceIgnoreCase(
                text,
                "Echo Golf Charlie Kilo",
                "EGCK");

            text = ReplaceIgnoreCase(
                text,
                "Echo Golf Foxtrot Alpha",
                "EGFA");

            return text;
        }

        private string ReplaceIgnoreCase(
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
