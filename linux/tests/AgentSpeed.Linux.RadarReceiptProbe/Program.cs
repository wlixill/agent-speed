using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Linux;

if(args.Length!=2 || args[0]!="--send-existing-configured-identity")
{
    Console.WriteLine("Explicit live-service verification only. Reads real local activity and sends the previously selected metrics to the configured Codex Radar service. Usage: --send-existing-configured-identity OUTPUT_DIRECTORY");
    return 2;
}
if(!OperatingSystem.IsLinux())throw new PlatformNotSupportedException("This explicit verifier requires Linux");
var output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
var storage=new AppStorage();
var identityPath=Path.Combine(storage.DataDirectory,"reporting","identity.json");
var before=JsonDocument.Parse(File.ReadAllBytes(identityPath));
var state=before.RootElement;
string Get(string key)=>state.GetProperty(key).GetString()!;
var endpoint=Get("Service");if(endpoint!="https://deng.codexradar.com")throw new InvalidOperationException("Unexpected recipient; verification stopped");
var username=Get("Username");var device=Get("DeviceId");
var keyHash=SHA256.HashData(Convert.FromBase64String(Get("PrivateKey")));
var key=state.TryGetProperty("ApiKey",out var apiKey)?apiKey.GetString():null;
var sections=state.GetProperty("Sections").EnumerateArray().Select(item=>ReportSections.Parse(item.GetString()!)).Where(item=>item.HasValue).Select(item=>item!.Value).ToHashSet();
if(sections.Count==0)throw new InvalidOperationException("No previously selected sections; fresh UI consent is required");
var backup=Path.Combine(output,"private-backup");Directory.CreateDirectory(backup);File.SetUnixFileMode(backup,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
File.Copy(identityPath,Path.Combine(backup,"identity.json"),false);File.SetUnixFileMode(Path.Combine(backup,"identity.json"),UnixFileMode.UserRead|UnixFileMode.UserWrite);
var settings=storage.Load().Validated();
var candidateVersion=File.ReadAllText(Path.Combine(Environment.CurrentDirectory,"VERSION")).Trim()+"-"
    +File.ReadAllText(Path.Combine(Environment.CurrentDirectory,"linux/DEBIAN_REVISION")).Trim();
using var cost=new LinuxCostService(storage,false,settings.Currency,version:candidateVersion);
using var report=new LinuxReportService(storage,version:candidateVersion);
report.Metric=settings.Metric;report.Redline=settings.Metric==SpeedMetric.Total?settings.RedlineTotal:settings.Redline;
var hub=new SpeedHub();
using var monitor=new LiveMonitor(hub,LinuxSources.Configuration(settings,LinuxSources.Root(settings,SourceKind.Codex)),pricing:cost.PriceBook);
hub.SnapshotChanged+=(_,snapshot)=>
{
    if(snapshot.Timestamp>DateTimeOffset.UnixEpoch)report.PriceVersion=cost.PriceBook.ReportPriceVersion(snapshot.Timestamp.ToUnixTimeMilliseconds());
    report.Observe(snapshot);
};
report.Start();monitor.Start();
var deadline=DateTimeOffset.UtcNow.AddSeconds(90);
while(hub.Snapshot.State.Kind!=MonitorStateKind.Running && DateTimeOffset.UtcNow<deadline)await Task.Delay(250);
if(hub.Snapshot.State.Kind!=MonitorStateKind.Running)throw new InvalidOperationException("Real source monitor not running; no synthetic metrics will be substituted");
await report.DiscoverAsync(endpoint);
var service=ReportServiceUrl.Parse(endpoint)!;
var oldDeclaration=ReportDiscovery.Parse(Encoding.UTF8.GetBytes(Get("Discovery")),service,out _)!;
if(report.DraftDiscovery is null || ReportConsentPin.Of(oldDeclaration).NeedsReview(report.DraftDiscovery,key is not null))
    throw new InvalidOperationException("Service declaration changed; fresh UI consent is required");
await report.EnableWithPlatformAsync(username,key,sections,LinuxReportPlatform.WindowsCompatibility);
Console.WriteLine(JsonSerializer.Serialize(new { operation="registration",report.Enabled,mode=report.PlatformMode.ToString(),report.Status,diagnostic=report.Diagnostics }));
if(!report.Enabled)throw new InvalidOperationException("Existing-identity registration failed: "+report.Diagnostics.LastResult);
using var http=new HttpClient(){Timeout=TimeSpan.FromSeconds(15)};http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
var reader="https://speed-api.codexradar.com/api/agent-speed/public/leaderboard?view=live&metric=live&limit=1&offset=0&focus="+Uri.EscapeDataString(username);
DateTimeOffset lastRead=DateTimeOffset.MinValue;bool found=false,online=false,nameMatches=false;JsonElement? receipt=null;
deadline=DateTimeOffset.UtcNow.AddSeconds(150);
while(DateTimeOffset.UtcNow<deadline)
{
    await Task.Delay(1000);
    if(!report.Enabled)throw new InvalidOperationException("Service stopped reports: "+report.Diagnostics.LastResult);
    if(report.Diagnostics.ReportsAccepted>=2 && DateTimeOffset.UtcNow-lastRead>TimeSpan.FromSeconds(10))
    {
        lastRead=DateTimeOffset.UtcNow;
        try
        {
            using var response=await http.GetAsync(reader);if(!response.IsSuccessStatusCode)continue;
            using var json=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            var root=json.RootElement;
            if(root.TryGetProperty("demo",out var demo) && demo.ValueKind==JsonValueKind.True)throw new InvalidOperationException("Demo data cannot establish live receipt");
            if(root.TryGetProperty("focus",out var focus) && focus.TryGetProperty("found",out var present) && present.ValueKind==JsonValueKind.True
                && focus.TryGetProperty("row",out var row) && row.ValueKind==JsonValueKind.Object)
            {
                string Normalize(string text)=>text.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
                nameMatches=Normalize(row.GetProperty("username").GetString()!)==Normalize(username);
                online=row.TryGetProperty("online",out var current)&&current.ValueKind==JsonValueKind.True;
                found=true;
                var allowed=new[]{"publicId","rank","online","onlineDevices","value","updatedAt","ageSeconds","activity","generating","totals"};
                var safe=row.EnumerateObject().Where(item=>allowed.Contains(item.Name)).ToDictionary(item=>item.Name,item=>item.Value.Clone());
                receipt=JsonSerializer.SerializeToElement(safe);
                Console.WriteLine(JsonSerializer.Serialize(new { operation="public-live-receipt",found,online,nameMatches,report.Diagnostics.ReportsAccepted }));
                if(online && nameMatches && hub.Snapshot.GeneratingCount>0)break;
            }
        }
        catch(HttpRequestException){ }
    }
}
using var after=JsonDocument.Parse(File.ReadAllBytes(identityPath));var saved=after.RootElement;
var preserved=saved.GetProperty("DeviceId").GetString()==device && saved.GetProperty("Username").GetString()==username
    && SHA256.HashData(Convert.FromBase64String(saved.GetProperty("PrivateKey").GetString()!)).AsSpan().SequenceEqual(keyHash);
var result=new { clientVersion=candidateVersion,fixtureDataUsed=false,report.Enabled,mode=report.PlatformMode.ToString(),identityPreserved=preserved,report.Diagnostics,
    realMonitor=new { state=hub.Snapshot.State.Kind.ToString(),hub.Snapshot.GeneratingCount,hub.Snapshot.Live,hub.Snapshot.Today },
    publicReceipt=new {found,online,nameMatches,row=receipt}};
File.WriteAllText(Path.Combine(output,"receipt.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
File.SetUnixFileMode(Path.Combine(output,"receipt.json"),UnixFileMode.UserRead|UnixFileMode.UserWrite);
if(report.Diagnostics.ReportsAccepted<2 || !found || !online || !nameMatches || !preserved)
    throw new InvalidOperationException("Signed report acceptance or own public leaderboard receipt not fully confirmed; inspect local receipt");
Console.WriteLine("PASS real signed reports accepted, own live leaderboard row online, real activity only, existing identity retained.");
return 0;
