using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using H145FlightPlanner.Logic;
using H145FlightPlanner.Models;

namespace H145FlightPlanner.Services
{
    public class LocalAiInterpreterService
    {
        private readonly LocalAiModelManager _modelManager = new();

        public async Task<FlightPlanRequest> InterpretAsync(
            string userInstruction,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userInstruction))
                return new FlightPlanRequest();

            try
            {
                (string runtime, string model) =
                    await _modelManager.EnsureReadyAsync(cancellationToken);

                string prompt = BuildPrompt(userInstruction);
                string output =
                    await RunModelAsync(runtime, model, prompt, cancellationToken);

                SmartRoutePlan? plan = ParsePlan(output);

                if (plan == null ||
                    string.IsNullOrWhiteSpace(plan.Start) ||
                    plan.Steps.Count == 0)
                {
                    throw new InvalidOperationException(
                        "The route-understanding model did not return a usable plan.");
                }

                NormalizePlan(plan);

                return new FlightPlanRequest
                {
                    Departure = IsIcao(plan.Start) ? plan.Start.ToUpperInvariant() : string.Empty,
                    Destination = IsIcao(plan.End) ? plan.End.ToUpperInvariant() : string.Empty,
                    RouteType = "SMART",
                    AltitudeFeet = plan.AltitudeFeet,
                    FlightRules = plan.FlightRules,
                    SmartPlan = plan,
                    RequestedLocations = BuildLocations(plan)
                };
            }
            catch
            {
                // Safety fallback: the old parser remains available if the local model
                // cannot initialize. The AI path is primary; this only prevents the
                // existing app from becoming unusable during a model/runtime outage.
                return FlightPlanCommandParser.Parse(userInstruction);
            }
        }

        private static string BuildPrompt(string userInstruction)
        {
            return $$"""
You are the silent route-intent compiler inside a helicopter flight-plan generator.
You are NOT a chatbot. Do not explain yourself. Output one JSON object only.

Read the user's complete instruction as a human pilot/planner would. Preserve every meaningful detail and infer obvious sequencing from natural language. Do not require fixed trigger phrases. Do not invent airport codes or place names. A four-letter ICAO remains exactly as spoken/typed. Ordinary places can be towns, beaches, docks, islands, headlands, regions or landmarks.

Your job is ONLY to understand intent and produce ordered route actions. You do not invent coordinates. Another part of the program performs map searches and creates the geometry.

Supported actions:
- DIRECT: travel from the current point to another location.
- COASTLINE_ALONG: join and tightly trace the real outer land/sea edge from one location toward another. Use this for phrases such as follow/hug/trace the coast, shoreline, outer edge, up/down the coast, or equivalent wording.
- COASTLINE_AROUND: tightly trace a complete outer edge around a named island/landmass/place, returning to the same point on that outline before continuing.
- ORBIT: orbit/circle a named place.
- RETURN: return to a stated earlier airport/location.
- END: finish at a stated location.

When the instruction says "around the coastline" or similar, this means trace the actual outer land edge in detail, not a loose circle. When it says "follow the coast down/up", direction is implied by the destination and should not cause shortcuts across land. If the user combines several instructions, create several ordered steps. If the user says "returning to X", include RETURN to X at the end.

Output this exact JSON shape:
{
  "start": "starting airport or place",
  "end": "final airport or place",
  "altitudeFeet": 1500,
  "flightRules": "VFR",
  "summary": "short internal summary",
  "steps": [
    {
      "action": "DIRECT|COASTLINE_ALONG|COASTLINE_AROUND|ORBIT|RETURN|END",
      "location": "named place when relevant",
      "from": "explicit start of this action if stated",
      "to": "explicit end of this action if stated",
      "direction": "up|down|north|south|east|west|clockwise|counterclockwise|",
      "keepCloseToEdge": true,
      "completeLoop": false,
      "notes": "important user detail, otherwise empty"
    }
  ]
}

Rules:
- Use null for altitudeFeet only if no altitude was given.
- Use an empty string for flightRules only if neither VFR nor IFR was given.
- Never add an action merely because it appeared in these instructions.
- Do not wrap JSON in markdown.

USER INSTRUCTION:
{{userInstruction}}
""";
        }

        private static async Task<string> RunModelAsync(
            string runtime,
            string model,
            string prompt,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = runtime,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(model);
            startInfo.ArgumentList.Add("--ctx-size");
            startInfo.ArgumentList.Add("4096");
            startInfo.ArgumentList.Add("--temp");
            startInfo.ArgumentList.Add("0.1");
            startInfo.ArgumentList.Add("--top-p");
            startInfo.ArgumentList.Add("0.9");
            startInfo.ArgumentList.Add("--no-display-prompt");
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("1200");
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(prompt);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken);

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                throw new InvalidOperationException(
                    "The local route-understanding model failed to run. " + stderr);
            }

            return stdout;
        }

        private static SmartRoutePlan? ParsePlan(string output)
        {
            int first = output.IndexOf('{');
            int last = output.LastIndexOf('}');

            if (first < 0 || last <= first)
                return null;

            string json = output.Substring(first, last - first + 1);

            return JsonSerializer.Deserialize<SmartRoutePlan>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
        }

        private static void NormalizePlan(SmartRoutePlan plan)
        {
            plan.Start = plan.Start.Trim();
            plan.End = plan.End.Trim();
            plan.FlightRules = plan.FlightRules.Trim().ToUpperInvariant();

            foreach (SmartRouteStep step in plan.Steps)
            {
                step.Action = step.Action.Trim().ToUpperInvariant();
                step.Location = step.Location.Trim();
                step.From = step.From.Trim();
                step.To = step.To.Trim();
                step.Direction = step.Direction.Trim().ToLowerInvariant();

                if (step.Action == "COASTLINE_AROUND")
                {
                    step.KeepCloseToEdge = true;
                    step.CompleteLoop = true;
                }

                if (step.Action == "COASTLINE_ALONG")
                {
                    step.KeepCloseToEdge = true;
                }
            }
        }

        private static bool IsIcao(string value) =>
            Regex.IsMatch(value ?? string.Empty, @"^[A-Za-z]{4}$");

        private static List<string> BuildLocations(SmartRoutePlan plan)
        {
            var result = new List<string>();

            void Add(string value)
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    !result.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(value);
                }
            }

            Add(plan.Start);
            Add(plan.End);

            foreach (SmartRouteStep step in plan.Steps)
            {
                Add(step.Location);
                Add(step.From);
                Add(step.To);
            }

            return result;
        }
    }
}
