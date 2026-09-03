using System;
using System.Speech.Synthesis;

namespace H145FlightPlanner.Speech
{
    public class SpeechService
    {
        private readonly SpeechSynthesizer _synthesizer;

        public SpeechService()
        {
            _synthesizer = new SpeechSynthesizer();

            _synthesizer.Volume = 100;
            _synthesizer.Rate = 0;
        }

        public void Speak(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            string pronunciationText =
                PronunciationDictionary.Apply(text);

            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.SpeakAsync(pronunciationText);
        }

        public void Stop()
        {
            _synthesizer.SpeakAsyncCancelAll();
        }
    }
}
