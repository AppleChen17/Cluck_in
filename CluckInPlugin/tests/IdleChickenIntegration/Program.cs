using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Loupedeck;
using Loupedeck.CluckInPlugin;

var root = Directory.GetCurrentDirectory();
if(!File.Exists(Path.Combine(root,"src/chicken/api.py"))) throw new Exception("Run from repository root");
Environment.SetEnvironmentVariable("PATH", Path.Combine(root,"CluckInPlugin/bin/Debug/bin") + Path.PathSeparator +
    (OperatingSystem.IsWindows() ? @"C:\Program Files\Logi\LogiPluginService;" : "") + Environment.GetEnvironmentVariable("PATH"));
var probe = new TcpListener(IPAddress.Loopback, 0);
probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
var url = $"http://127.0.0.1:{port}/";
Environment.SetEnvironmentVariable("CLUCKIN_CHICKEN_URL", url);
Environment.SetEnvironmentVariable("CLUCKIN_INPUT_EVENT_URL", url + "unused-input-event");
var start = new ProcessStartInfo(args.FirstOrDefault() ?? "python") { UseShellExecute=false, CreateNoWindow=true };
foreach(var arg in new[]{"-B","-m","uvicorn","api:app","--app-dir","src/chicken","--host","127.0.0.1","--port",port.ToString(),"--log-level","error"}) start.ArgumentList.Add(arg);
var statePath = Path.Combine(Path.GetTempPath(), $"cluckin-idle-{Guid.NewGuid():N}.json");
start.Environment["CLUCKIN_CHICKEN_STATE_PATH"] = statePath;
using var api = Process.Start(start)!;
using var http = new HttpClient { BaseAddress = new Uri(url), Timeout=TimeSpan.FromSeconds(2) };
var flags = BindingFlags.NonPublic | BindingFlags.Static;
var assembly = typeof(MainController).Assembly;
var adapter = assembly.GetType("Loupedeck.CluckInPlugin.IdleChickenAnimation")!;
var samples = new ConcurrentQueue<(string Mood,string Frame,byte[] Image)>();
var errors = new ConcurrentQueue<Exception>();
var key = new FocusControlCommand();
var commandFlags = BindingFlags.Instance | BindingFlags.NonPublic;
BitmapImage Image() => (BitmapImage)typeof(FocusControlCommand).GetMethod("GetCommandImage",commandFlags)!.Invoke(key,new object[]{"",PluginImageSize.Width90})!;
string Text(PluginDynamicCommand command) => (string)command.GetType().GetMethod("GetCommandDisplayName",commandFlags)!.Invoke(command,new object[]{"",PluginImageSize.Width90})!;
string Label() => (string)typeof(FocusControlCommand).GetMethod("GetCommandDisplayName",commandFlags)!.Invoke(key,new object[]{"",PluginImageSize.Width90})!;
void Mode(CluckInMode mode) {
 typeof(MainController).GetField("<CurrentMode>k__BackingField",flags)!.SetValue(null,mode);
 ((Action)typeof(MainController).GetField("ModeChanged",flags)!.GetValue(null)!)();
}
int checks=0;
void Check(bool ok,string message){if(!ok)throw new Exception(message); checks++;}
async Task Until(Func<bool> ready,int seconds=12){
 var deadline=DateTime.UtcNow.AddSeconds(seconds);
 while(!ready()) { if(!errors.IsEmpty)throw errors.First(); if(DateTime.UtcNow>deadline)throw new Exception("Timed out awaiting animation"); await Task.Delay(25); }
}
Action frameChanged = () => {
 try {
  var view=http.GetFromJsonAsync<JsonElement>("view").GetAwaiter().GetResult();
  using var image=Image();
  samples.Enqueue((view.GetProperty("mood").GetString()!,view.GetProperty("chicken").GetString()!,image.ToArray()));
 } catch(Exception ex){errors.Enqueue(ex);}
};
try {
 await Until(()=> { try {return http.GetAsync("health").GetAwaiter().GetResult().IsSuccessStatusCode;} catch{return false;} });
 adapter.GetEvent("FrameChanged",flags)!.GetAddMethod(true)!.Invoke(null,new object[]{frameChanged});
 adapter.GetMethod("Start",flags)!.Invoke(null,null);
 Mode(CluckInMode.Idle);
 await Until(()=>samples.Count>=5);
 var idle=samples.ToArray().Take(5).ToArray();
 Check(idle.Select(x=>x.Frame).SequenceEqual(new[]{"idle_00.png","idle_01.png","idle_02.png","idle_03.png","idle_00.png"}),"Idle sequence");
 Check(idle.Select(x=>Convert.ToBase64String(x.Image)).Distinct().Count()>1,"Rendered Idle pixels must change");
 Check(Label()=="\u200B","No Idle timer label");
 Check(Text(new CounterCommand())=="FOCUS" && Text(new MessageCommand())=="PET","Idle native action labels");
 Check(Text(new AutomationCommand())=="0\nFEED".Replace("\n",Environment.NewLine),"Empty inventory native label");
 var beforeEmpty = samples.Count;
 MainController.HandleKeyEvent(3); await Until(()=>samples.Count>beforeEmpty);
 Check(samples.Last().Mood=="idle","Empty feed does not animate");
 using(var credit=await http.PostAsJsonAsync("event",new {type="RECORD_FOCUS",payload=new {sessionId="test-completed",seconds=9318,completed=true}})) credit.EnsureSuccessStatusCode();
 await Until(()=>Text(new AutomationCommand()).StartsWith("1"));
 Check(Text(new TimerHourCommand())==$"02{Environment.NewLine}HR","Idle cumulative hours");
 Check(Text(new TimerMinuteCommand())==$"35{Environment.NewLine}MIN","Idle cumulative minutes");
 Check(Text(new TimerSecondCommand())==$"18{Environment.NewLine}SEC","Idle cumulative seconds");
 var selected=MainController.CurrentFocusTimerField;
 foreach(var button in new[]{4,6,7,8,9}) MainController.HandleKeyEvent(button);
 Check(MainController.CurrentFocusTimerField==selected,"Idle statistics keys do not edit timer");
 foreach(var (button,mood,count) in new[]{(2,"pet",12),(3,"feed",18)}) {
  samples.Clear(); MainController.HandleKeyEvent(button);
  await Until(()=>samples.Any(x=>x.Mood==mood));
  await Until(()=>samples.LastOrDefault().Mood=="idle");
  var frames=samples.Where(x=>x.Mood==mood).Select(x=>x.Frame).ToArray();
  Check(frames.SequenceEqual(Enumerable.Range(0,count).Select(i=>$"{mood}_{i:00}.png")), mood+" complete Python sequence");
  Check(samples.Last().Frame=="idle_00.png",mood+" returns Idle");
 }
 Check(Text(new TaskCommand())==$"1{Environment.NewLine}PATS","Accepted pats displayed");
 Check(Text(new FocusStopCommand())==$"1{Environment.NewLine}FED","Successful feeds displayed");
 Check(Text(new AutomationCommand())==$"0{Environment.NewLine}FEED","Feed consumed exactly once");
 MainController.HandleKeyEvent(2); await Until(()=>samples.Last().Mood=="pet");
 Mode(CluckInMode.Focus); await Task.Delay(300);
 Check(Text(new CounterCommand())=="WORK" && Text(new MessageCommand())=="Messages" &&
       Text(new AutomationCommand())=="AI OFF" && Text(new TaskCommand())=="Task" &&
       Text(new FocusStopCommand())=="END","Focus mappings unchanged");
 var stoppedCount=samples.Count;
 var stoppedView=await http.GetStringAsync("view"); await Task.Delay(1100);
 Check(samples.Count==stoppedCount && await http.GetStringAsync("view")==stoppedView,"Focus stops the frame clock");
 assembly.GetType("Loupedeck.CluckInPlugin.PluginResources")!.GetMethod("Init")!.Invoke(null,new object[]{assembly});
 var renderer=assembly.GetType("Loupedeck.CluckInPlugin.ButtonImageRenderer")!;
 foreach(var (state,label) in new[]{(FocusTimerControlState.Ready,"START"),(FocusTimerControlState.Running,"PAUSE"),(FocusTimerControlState.Paused,"RESUME"),(FocusTimerControlState.Completed,"START")}) {
  typeof(MainController).GetField("<CurrentFocusTimerState>k__BackingField",flags)!.SetValue(null,state);
  using var expected=(BitmapImage)renderer.GetMethod("DrawDeskState")!.Invoke(null,new object[]{state,MainController.FocusProgress})!;
  using var actual=Image();
  Check(Label()==label && actual.ToArray().SequenceEqual(expected.ToArray()),"Focus Key 5 preserved: "+state);
 }
 Mode(CluckInMode.Idle); samples.Clear(); await Until(()=>samples.Count>=2);
 Check(samples.First().Frame=="idle_00.png","Re-enter Idle resets Python state");
 adapter.GetMethod("Stop",flags)!.Invoke(null,null); await Task.Delay(200);
 stoppedCount=samples.Count; await Task.Delay(700);
 Check(samples.Count==stoppedCount,"Unload cancels updates");
 Check(errors.IsEmpty,"No rendering/callback errors");
 var persisted=JsonDocument.Parse(File.ReadAllText(statePath)).RootElement;
 Check(persisted.GetProperty("feedCount").GetInt64()==0 && persisted.GetProperty("patCount").GetInt64()==2 &&
       persisted.GetProperty("successfulFeedCount").GetInt64()==1 && persisted.GetProperty("totalFocusSeconds").GetInt64()==9318,
       "Canonical API statistics persisted");
 Console.WriteLine($"PASS: {checks} checks against real Python API: looping pixels, full pet/feed sequences, mode switch, Focus images/labels, unload.");
} finally {
 adapter.GetMethod("Stop",flags)!.Invoke(null,null);
 if(!api.HasExited){api.Kill(entireProcessTree:true);api.WaitForExit();}
 if(File.Exists(statePath))File.Delete(statePath);
}
