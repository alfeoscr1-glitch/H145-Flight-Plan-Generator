using System;

namespace H145FlightPlanner.Speech
{
    public class SpeechService
    {
        public event EventHandler<string>? SpeechRecognized;
        public event EventHandler<string>? SpeechError;

        public void StartListening()
        {
            // Whisper microphone integration will be connected here.
        }

        public void StopListening()
        {
            // Whisper microphone integration will be connected here.
        }

        protected void RaiseSpeechRecognized(string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                SpeechRecognized?.Invoke(this, text);
            }
        }

        protected void RaiseSpeechError(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                SpeechError?.Invoke(this, message);
            }
        }
    }
}
