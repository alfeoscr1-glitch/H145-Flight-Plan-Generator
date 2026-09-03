using System;
using System.Collections.Generic;

namespace H145FlightPlanner.Speech
{
    public static class PronunciationDictionary
    {
        private static readonly Dictionary<string, string> Pronunciations =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "Anglesey", "Anglesey" },
                { "Fishguard", "Fishguard" },
                { "Pembrokeshire", "Pembrokeshire" },
                { "Aberystwyth", "Aberystwyth" },
                { "H145", "H one four five" }
            };

        public static string Apply(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            foreach (var entry in Pronunciations)
            {
                text = text.Replace(
                    entry.Key,
                    entry.Value,
                    StringComparison.OrdinalIgnoreCase);
            }

            return text;
        }
    }
}
