using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using H145FlightPlanner.Models;

namespace H145FlightPlanner.Services
{
    // Silent local intent interpreter. It never speaks to the user and never
    // invents coordinates. Its only job is to convert free-form instructions
    // into an ordered route plan for deterministic map/routing code.
    public class LocalAiInterpreterService
    {
        private readonly LocalAiModelManager _modelManager = new();

        public async Task<FlightPlanRequest> InterpretAsync(
            string userInstruction,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userInstruction))
                return new FlightPlanRequest();

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
                    "The local route-understanding model could not turn the instruction into a usable route plan.");
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

        private static string BuildPrompt(string userInstruction)
        {
            return $$"""
You are the invisible route-intent compiler inside a UK helicopter flight-plan generator.
You are NOT a chatbot. Never speak to the pilot. Never explain. Output exactly one JSON object and nothing else.

Understand the entire instruction by meaning, not by matching fixed trigger phrases. The pilot may phrase the same idea in completely new ways. Preserve every named place and every qualifier exactly enough for a map search. For example, "Anglesey in Wales" must remain a Wales-qualified Anglesey reference rather than being shortened to an ambiguous name.

You do NOT create coordinates. You do NOT guess map shapes. You only compile the requested journey into ordered actions. A separate geographic engine performs factual place search, measurements, coastline tracing and validation.

The plan can contain ANY NUMBER OF STEPS. Multi-leg requests are normal. If the pilot asks for four, ten or more successive places, output all of them in order. Do not collapse them into one leg.

Supported actions:
DIRECT
- Travel from the current location to a location without an edge-following instruction.

COASTLINE_ALONG
- Join the real land/sea edge and follow it continuously from one requested point/place toward another.
- Use for follow/hug/trace/stick to the coast, shoreline, sea edge, outer edge, down the coast, up the coast, around headlands while continuing, or equivalent natural wording.
- "down and around" / "up and around" means continue along the coast in the requested direction while following the outside of bays, peninsulas and headlands instead of shortcutting across land.
- Do not turn this into a straight line merely because the two endpoints are far apart.

COASTLINE_AROUND
- Trace one complete detailed circuit around a named island or landmass, then continue from the circuit.
- "around the coastline of the Isle of Man" means a complete real edge trace, not a generic circle.

ORBIT
- Orbit/circle a named geographic place. Preserve qualifiers such as town/city/country/region.

RETURN
- Return to an earlier named airport/place.

END
- Finish at a named airport/place.

Reason about sequencing. Examples of reasoning rules, not phrases to memorize:
- "Start A, coast to B, then coast to C, then finish D" => separate ordered legs.
- "Start A, around island X, return A" => travel to X if needed, complete X circuit, then return A.
- "Start A, go to town X, follow coast from X to B, then coast from B to C" => DIRECT X, COASTLINE_ALONG X->B, COASTLINE_ALONG B->C.
- If a coastal action starts from the current location, its from field can be empty. If the pilot explicitly names its start, preserve it.
- Never invent an ICAO or place that the pilot did not request.

Output this JSON shape:
{
  "start": "starting airport or place",
  "end": "final airport or place if clear, otherwise empty",
  "altitudeFeet": 1500,
  "flightRules": "VFR",
  "summary": "short internal route summary",
  "steps": [
    {
      "action": "DIRECT|COASTLINE_ALONG|COASTLINE_AROUND|ORBIT|RETURN|END",
      "location": "named place when relevant",
      "from": "explicit start for this action if stated",
      "to": "explicit destination for this action if stated",
      "direction": "up|down|north|south|east|west|clockwise|counterclockwise|",
      "keepCloseToEdge": true,
      "completeLoop": false,
      "avoidLand": true,
      "notes": "important route detail or empty"
    }
  ]
}

Use null for altitudeFeet only if no altitude was stated. Use an empty string for flightRules if neither VFR nor IFR was stated. For coast-following actions set keepCloseToEdge=true and avoidLand=true. For complete circuits set completeLoop=true. Do not wrap the JSON in markdown.

PILOT INSTRUCTION:
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
            startInfo.ArgumentList.Add("6144");
            startInfo.ArgumentList.Add("--temp");
            startInfo.ArgumentList.Add("0.05");
            startInfo.ArgumentList.Add("--top-p");
            startInfo.ArgumentList.Add("0.9");
            startInfo.ArgumentList.Add("--no-display-prompt");
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("1800");
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
            plan.Start = (plan.Start ?? string.Empty).Trim();
            plan.End = (plan.End ?? string.Empty).Trim();
            plan.FlightRules = (plan.FlightRules ?? string.Empty).Trim().ToUpperInvariant();
            plan.Summary = (plan.Summary ?? string.Empty).Trim();
            plan.Steps ??= new List<SmartRouteStep>();

            foreach (SmartRouteStep step in plan.Steps)
            {
                step.Action = (step.Action ?? string.Empty).Trim().ToUpperInvariant();
                step.Location = (step.Location ?? string.Empty).Trim();
                step.From = (step.From ?? string.Empty).Trim();
                step.To = (step.To ?? string.Empty).Trim();
                step.Direction = (step.Direction ?? string.Empty).Trim().ToLowerInvariant();
                step.Notes = (step.Notes ?? string.Empty).Trim();

                if (step.Action == "COASTLINE_AROUND")
                {
                    step.KeepCloseToEdge = true;
                    step.CompleteLoop = true;
                    step.AvoidLand = true;
                }
                else if (step.Action == "COASTLINE_ALONG")
                {
                    step.KeepCloseToEdge = true;
                    step.AvoidLand = true;
                }
            }
        }

        private static List<string> BuildLocations(SmartRoutePlan plan)
        {
            var result = new List<string>();

            void Add(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return;

                string text = value.Trim();
                if (!result.Contains(text, StringComparer.OrdinalIgnoreCase))
                    result.Add(text);
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

        private static bool IsIcao(string value) =>
            Regex.IsMatch((value ?? string.Empty).Trim(), @"^[A-Za-z]{4}$");
    }
}
