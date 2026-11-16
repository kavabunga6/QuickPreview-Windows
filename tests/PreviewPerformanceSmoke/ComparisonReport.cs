using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

internal static class ComparisonReport
{
    internal static void Write(string directory)
    {
        if(!File.Exists(Path.Combine(directory,"before.json"))||!File.Exists(Path.Combine(directory,"after.json")))return;
        using var before=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"before.json")));
        using var after=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"after.json")));
        var first=before.RootElement.GetProperty("Results").EnumerateArray().ToDictionary(row=>row.GetProperty("Name").GetString()!);
        var second=after.RootElement.GetProperty("Results").EnumerateArray().ToDictionary(row=>row.GetProperty("Name").GetString()!);
        var output=new StringBuilder("# QuickPreview bounded performance comparison\n\n");
        output.AppendLine("One run per payload on the same machine and deterministic fixtures. CPU means core milliseconds per second (1000 = one logical core); memory is endpoint private MiB. Timing includes common test probes. Small differences may reflect background activity and CPU timer granularity.\n");
        output.AppendLine("| Scenario | Seconds before → after | App CPU before → after | WebView CPU before → after | WPF lag p95 before → after (ms) | WPF max before → after (ms) | App private before → after (MiB) | WebView private before → after (MiB) |");
        output.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach(var name in first.Keys.Where(second.ContainsKey))
        {
            var a=first[name];var b=second[name];
            string Pair(string field)=>Number(a,field)+" → "+Number(b,field);
            output.AppendLine($"| {name} | {Pair("Seconds")} | {Pair("AppCpuCoreMsPerSecond")} | {Pair("WebViewCpuCoreMsPerSecond")} | {Pair("DispatcherLagP95Ms")} | {Pair("DispatcherLagMaxMs")} | {Pair("AppPrivateMiBAtEnd")} | {Pair("WebViewPrivateMiBAtEnd")} |");
        }
        output.AppendLine("\nThe original renderer already rendered on demand. Zero idle frames in the baseline must not be described as fixing a continuous idle loop. Suspended cores are observed without waking their JavaScript, with no inferred frame delta.\n");
        foreach(var name in new[]{"stl-1m-rapid-cancel-to-cube","obj-1m-cancel-by-hide"})
        {
            if(!first.TryGetValue(name,out var a)||!second.TryGetValue(name,out var b))continue;
            output.AppendLine($"- {name}: before `{a.GetProperty("ActionNote").GetString()?.ReplaceLineEndings(" ")}`; after `{b.GetProperty("ActionNote").GetString()?.ReplaceLineEndings(" ")}`.");
        }
        output.AppendLine("\nA budget rejection protects responsiveness and is not a successful full import of the 1M-triangle file. The 200k cases remain valid rendered workloads.\n");
        output.AppendLine("The origin-poll phase includes one synchronous test-only origin acquisition plus timer/worker setup. Its maximum and total CPU include that setup; its p95 better reflects the repeated polling workload. The real application acquires its origin asynchronously. The initial model-load phase also includes native WebView control initialization.\n");
        output.AppendLine("- Baseline DLL SHA256: `"+before.RootElement.GetProperty("AppSha256").GetString()+"`.");
        output.AppendLine("- After DLL SHA256: `"+after.RootElement.GetProperty("AppSha256").GetString()+"`.");
        File.WriteAllText(Path.Combine(directory,"comparison.md"),output.ToString());
    }
    private static string Number(JsonElement row,string name)=>row.GetProperty(name).GetDouble().ToString("0.00",CultureInfo.InvariantCulture);
}
