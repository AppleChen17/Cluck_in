namespace Loupedeck.CluckInPlugin;

using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SkiaSharp;

// Transport and frame clock only. All animation state belongs to chicken/state_machine.py.
internal sealed class IdleChickenAnimation : IDisposable{
    internal static IdleChickenAnimation Current { get; private set; }
    internal static event Action FrameChanged;
    internal static event Action StatisticsChanged;
    private ChickenView _view;

    internal static String Statistic(Func<ChickenView, Int64> select, String label){
        var view = Current == null ? null : Volatile.Read(ref Current._view);
        return $"{(view == null ? "--" : select(view).ToString())}{Environment.NewLine}{label}";
    }

    internal static String FocusTime(Int32 part){
        var view = Current == null ? null : Volatile.Read(ref Current._view);
        var seconds = view?.TotalFocusSeconds ?? 0;
        var value = part == 0 ? seconds / 3600 : part == 1 ? (seconds / 60) % 60 : seconds % 60;
        var label = part == 0 ? "HR" : part == 1 ? "MIN" : "SEC";
        return $"{(view == null ? "--" : value.ToString("00"))}{Environment.NewLine}{label}";
    }

    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<(Int32 Generation, String Event)> _events =
        Channel.CreateUnbounded<(Int32, String)>();
    private readonly Task _worker;
    private Int32 _generation;
    private Byte[] _frame;

    internal static void Start(){
        Current ??= new IdleChickenAnimation();
    }

    internal static void Stop(){
        var current = Current;
        Current = null;
        current?.Dispose();
    }

    private IdleChickenAnimation(){
        MainController.ModeChanged += this.OnModeChanged;
        MainController.ActionRequested += this.OnActionRequested;
        this._worker = this.RunAsync();
    }

    private void OnModeChanged(){
        var generation = Interlocked.Increment(ref this._generation);
        Volatile.Write(ref this._frame, null);
        this._events.Writer.TryWrite((generation, null));
    }

    private void OnActionRequested(CluckInEvent request){
        if(request.Mode != CluckInMode.Idle){
            return;
        }
        var eventName = request.Action switch{
            CluckInAction.PetChicken => "PET_CHICKEN",
            CluckInAction.FeedChicken => "FEED_CHICKEN",
            _ => null
        };
        if(eventName != null){
            this._events.Writer.TryWrite((Volatile.Read(ref this._generation), eventName));
        }
    }

    private async Task RunAsync(){
        var token = this._stop.Token;
        using var client = new HttpClient{
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("CLUCKIN_CHICKEN_URL")
                ?? "http://127.0.0.1:8001/"),
            Timeout = TimeSpan.FromSeconds(2)
        };
        var generation = -1;
        var nextFrameAt = 0L;
        var failed = false;
        try{
            while(!token.IsCancellationRequested){
                if(MainController.CurrentMode != CluckInMode.Idle){
                    await this._events.Reader.ReadAsync(token);
                    continue;
                }

                var requestedGeneration = Volatile.Read(ref this._generation);
                String eventName;
                if(generation != requestedGeneration){
                    eventName = "SET_MOOD";
                }
                else if(this._events.Reader.TryRead(out var pending)){
                    if(pending.Generation != generation || pending.Event == null){
                        continue;
                    }
                    eventName = pending.Event;
                }
                else{
                    var delay = nextFrameAt - Environment.TickCount64;
                    if(delay > 0){
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                        wait.CancelAfter(TimeSpan.FromMilliseconds(delay));
                        try{
                            await this._events.Reader.WaitToReadAsync(wait.Token);
                        }
                        catch(OperationCanceledException) when(!token.IsCancellationRequested){ }
                        continue;
                    }
                    eventName = "tick";
                }

                try{
                    // One worker serializes mode entry, pet/feed and ticks in arrival order.
                    Object payload = eventName == "SET_MOOD" ? new { mood = "idle" } : new { };
                    using var response = await client.PostAsJsonAsync(
                        "event", new { type = eventName, payload }, token);
                    response.EnsureSuccessStatusCode();
                    var view = await response.Content.ReadFromJsonAsync<ChickenView>(token);
                    if(view == null || view.Fps <= 0 || view.DisplayKey != 5 ||
                        String.IsNullOrEmpty(view.Chicken) ||
                        view.Chicken != System.IO.Path.GetFileName(view.Chicken)){
                        throw new InvalidOperationException("Invalid Idle ChickenView");
                    }
                    var frame = await client.GetByteArrayAsync(
                        "frames/" + Uri.EscapeDataString(view.Chicken), token);
                    token.ThrowIfCancellationRequested();
                    if(requestedGeneration != Volatile.Read(ref this._generation) ||
                        MainController.CurrentMode != CluckInMode.Idle){
                        continue;
                    }
                    generation = requestedGeneration;
                    Volatile.Write(ref this._frame, frame);
                    var previous = Volatile.Read(ref this._view);
                    Volatile.Write(ref this._view, view);
                    if(previous == null || previous.FeedCount != view.FeedCount ||
                        previous.PatCount != view.PatCount || previous.SuccessfulFeedCount != view.SuccessfulFeedCount ||
                        previous.TotalFocusSeconds != view.TotalFocusSeconds){
                        StatisticsChanged?.Invoke();
                    }
                    nextFrameAt = Environment.TickCount64 + Math.Max(1, 1000 / view.Fps);
                    failed = false;
                    FrameChanged?.Invoke();
                }
                catch(Exception ex) when(!token.IsCancellationRequested){
                    if(!failed){
                        PluginLog.Warning(ex, "Idle chicken unavailable; run the chicken API on port 8001");
                    }
                    failed = true;
                    generation = -1;
                    await Task.Delay(1000, token);
                }
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){ }
    }

    internal BitmapImage Draw(PluginImageSize imageSize){
        using var dimensions = new BitmapBuilder(
            imageSize == PluginImageSize.None ? PluginImageSize.Width90 : imageSize);
        using var target = new SKBitmap(dimensions.Width, dimensions.Height);
        using var canvas = new SKCanvas(target);
        canvas.Clear(new SKColor(24, 24, 26));
        var bytes = Volatile.Read(ref this._frame);
        if(bytes != null){
            using var source = SKBitmap.Decode(bytes);
            if(source != null){
                // Python measures one common crop over all Idle/pet/feed frames.
                // No per-frame recentering or zoom; all visible content is retained.
                var crop = Volatile.Read(ref this._view)?.IdleCrop;
                var sourceArea = crop != null && crop.Width > 0 && crop.Height > 0 &&
                    crop.X >= 0 && crop.Y >= 0 && crop.X + crop.Width <= source.Width &&
                    crop.Y + crop.Height <= source.Height
                    ? new SKRect(crop.X, crop.Y, crop.X + crop.Width, crop.Y + crop.Height)
                    : new SKRect(0, 0, source.Width, source.Height);
                var scale = Math.Min(target.Width / sourceArea.Width, target.Height / sourceArea.Height);
                var width = sourceArea.Width * scale;
                var height = sourceArea.Height * scale;
                var x = (target.Width - width) / 2;
                var y = (target.Height - height) / 2;
                using var paint = new SKPaint{ FilterQuality = SKFilterQuality.None, IsAntialias = false };
                canvas.DrawBitmap(source, sourceArea, new SKRect(x, y, x + width, y + height), paint);
            }
        }
        using var image = SKImage.FromBitmap(target);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return BitmapImage.FromArray(data.ToArray());
    }

    public void Dispose(){
        MainController.ModeChanged -= this.OnModeChanged;
        MainController.ActionRequested -= this.OnActionRequested;
        this._stop.Cancel();
        // Do not block the SDK unload thread on an outstanding HTTP request.
        _ = this._worker.ContinueWith(_ => this._stop.Dispose(), TaskScheduler.Default);
    }

    internal sealed class CropArea{
        public Int32 X { get; set; }
        public Int32 Y { get; set; }
        public Int32 Width { get; set; }
        public Int32 Height { get; set; }
    }

    internal sealed class ChickenView{
        public String Chicken { get; set; }
        public Int32 Fps { get; set; }
        public Int32 DisplayKey { get; set; }
        public Int64 FeedCount { get; set; }
        public Int64 PatCount { get; set; }
        public Int64 SuccessfulFeedCount { get; set; }
        public Int64 TotalFocusSeconds { get; set; }
        public CropArea IdleCrop { get; set; }
    }
}
