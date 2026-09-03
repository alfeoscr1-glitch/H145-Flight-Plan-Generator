using System;

namespace H145FlightPlanner.Speech
{
    public static class SpeechConfiguration
    {
        public static string SpeechKey =>
            Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY") ?? string.Empty;

        public static string SpeechRegion =>
            Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION") ?? string.Empty;
    }
}
