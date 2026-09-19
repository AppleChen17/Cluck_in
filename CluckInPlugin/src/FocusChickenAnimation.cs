namespace Loupedeck.CluckInPlugin;

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SkiaSharp;

internal sealed class FocusChickenAnimation : IDisposable{
    internal static FocusChickenAnimation Current {get; private set;}
    internal static event Action FrameChanged;

    private readonly CancellationTokenSource _stop = new();

    private readonly Channel<Boolean> _wake =
        Channel.CreateUnbounded<Boolean>();

    private readonly Task _worker;

    private Int32 _generation;

    private FocusTimerControlState _observedState =
        MainController.CurrentFocusTimerState;

    private Byte[] _key5Frame;
    private Byte[] _key6Frame;

    private CropArea _key5Crop;
    private CropArea _key6Crop;

    internal static void Start(){
        Current ??= new FocusChickenAnimation();
    }

    internal static void Stop(){
        var current = Current;
        Current = null;
        current?.Dispose();
    }

    private FocusChickenAnimation(){
        MainController.ModeChanged +=
            this.OnModeChanged;

        MainController.FocusTimerChanged +=
            this.OnFocusTimerChanged;

        this._worker = this.RunAsync();

        this._wake.Writer.TryWrite(true);
    }

    private void OnModeChanged(){
        Interlocked.Increment(ref this._generation);
        this._wake.Writer.TryWrite(true);
    }

    private void OnFocusTimerChanged(){
        var state =
            MainController.CurrentFocusTimerState;

        if(state == this._observedState){
            return;
        }

        this._observedState = state;

        Interlocked.Increment(ref this._generation);
        this._wake.Writer.TryWrite(true);
    }

    private async Task RunAsync(){
        var token = this._stop.Token;

        using var client = new HttpClient{
            BaseAddress = new Uri(
                Environment.GetEnvironmentVariable(
                    "CLUCKIN_CHICKEN_URL"
                ) ?? "http://127.0.0.1:8001/"
            ),
            Timeout = TimeSpan.FromSeconds(2)
        };

        var appliedGeneration = -1;
        VisualSet visual = null;
        var frame = 0;
        var failed = false;

        try{
            while(!token.IsCancellationRequested){
                if(MainController.CurrentMode !=
                    CluckInMode.Focus)
                {
                    await this._wake.Reader.ReadAsync(token);
                    DrainWake();
                    appliedGeneration = -1;
                    continue;
                }

                var generation =
                    Volatile.Read(ref this._generation);

                var state =
                    MainController.CurrentFocusTimerState;

                if(
                    visual == null ||
                    appliedGeneration != generation
                ){
                    try{
                        visual =
                            await LoadVisualSet(
                                client,
                                state,
                                token
                            );

                        if(
                            generation !=
                                Volatile.Read(
                                    ref this._generation
                                ) ||
                            MainController.CurrentMode !=
                                CluckInMode.Focus ||
                            state !=
                                MainController
                                    .CurrentFocusTimerState
                        ){
                            continue;
                        }

                        appliedGeneration = generation;
                        frame = 0;
                        failed = false;
                    }
                    catch(Exception ex)
                        when(!token.IsCancellationRequested)
                    {
                        if(!failed){
                            PluginLog.Warning(
                                ex,
                                "Focus chicken frames unavailable; run chicken API on port 8001"
                            );
                        }

                        failed = true;

                        await WaitOrWake(
                            1000,
                            token
                        );

                        continue;
                    }
                }

                var key5 =
                    visual.Key5[
                        frame %
                        visual.Key5.Length
                    ];

                var key6 =
                    visual.Key6[
                        frame %
                        visual.Key6.Length
                    ];

                Volatile.Write(
                    ref this._key5Frame,
                    key5
                );

                Volatile.Write(
                    ref this._key6Frame,
                    key6
                );

                Volatile.Write(
                    ref this._key5Crop,
                    visual.Key5Crop
                );

                Volatile.Write(
                    ref this._key6Crop,
                    visual.Key6Crop
                );

                FrameChanged?.Invoke();

                frame++;

                await WaitOrWake(
                    Math.Max(
                        1,
                        1000 / visual.Fps
                    ),
                    token
                );
            }
        }
        catch(OperationCanceledException)
            when(token.IsCancellationRequested)
        {
        }
    }

    private async Task WaitOrWake(
        Int32 milliseconds,
        CancellationToken token)
    {
        using var timeout =
            CancellationTokenSource
                .CreateLinkedTokenSource(token);

        timeout.CancelAfter(milliseconds);

        try{
            await this._wake.Reader
                .WaitToReadAsync(timeout.Token);

            DrainWake();
        }
        catch(OperationCanceledException)
            when(!token.IsCancellationRequested)
        {
        }
    }

    private void DrainWake(){
        while(this._wake.Reader.TryRead(out _)){
        }
    }

    private static async Task<VisualSet> LoadVisualSet(
        HttpClient client,
        FocusTimerControlState state,
        CancellationToken token)
    {
        String[] key5Names;
        String[] key6Names;
        Int32 fps;

        switch(state){
            case FocusTimerControlState.Running:
                key5Names = new[]{
                    "start_00.png",
                    "start_01.png",
                    "start_02.png",
                    "start_03.png"
                };

                key6Names = new[]{
                    "nest_icon.png"
                };

                fps = 3;
                break;

            case FocusTimerControlState.Paused:
                key5Names = new[]{
                    "paused_00.png",
                    "paused_01.png",
                    "paused_02.png",
                    "paused_03.png"
                };

                key6Names = new[]{
                    "nest_icon.png"
                };

                fps = 3;
                break;

            case FocusTimerControlState.Ready:
            case FocusTimerControlState.Completed:
            default:
                key5Names = new[]{
                    "desk_empty.png"
                };

                key6Names = new[]{
                    "focused_00.png",
                    "focused_01.png",
                    "focused_02.png",
                    "focused_03.png",
                    "focused_04.png"
                };

                fps = 2;
                break;
        }

        var key5 =
            await LoadFrames(
                client,
                key5Names,
                token
            );

        var key6 =
            await LoadFrames(
                client,
                key6Names,
                token
            );

        return new VisualSet{
            Key5 = key5,
            Key6 = key6,
            Key5Crop = MeasureUnionCrop(key5),
            Key6Crop = MeasureUnionCrop(key6),
            Fps = fps
        };
    }

    private static async Task<Byte[][]> LoadFrames(
        HttpClient client,
        String[] names,
        CancellationToken token)
    {
        var result =
            new Byte[names.Length][];

        for(var i = 0; i < names.Length; i++){
            result[i] =
                await client.GetByteArrayAsync(
                    "frames/" +
                    Uri.EscapeDataString(
                        names[i]
                    ),
                    token
                );
        }

        return result;
    }

    private static CropArea MeasureUnionCrop(
        Byte[][] frames)
    {
        var left = Int32.MaxValue;
        var top = Int32.MaxValue;
        var right = -1;
        var bottom = -1;

        var canvasWidth = 0;
        var canvasHeight = 0;

        foreach(var bytes in frames){
            using var bitmap =
                SKBitmap.Decode(bytes);

            if(bitmap == null){
                continue;
            }

            canvasWidth =
                Math.Max(
                    canvasWidth,
                    bitmap.Width
                );

            canvasHeight =
                Math.Max(
                    canvasHeight,
                    bitmap.Height
                );

            for(var y = 0;
                y < bitmap.Height;
                y++)
            {
                for(var x = 0;
                    x < bitmap.Width;
                    x++)
                {
                    if(
                        bitmap.GetPixel(x, y).Alpha ==
                        0
                    ){
                        continue;
                    }

                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        if(
            right < left ||
            bottom < top
        ){
            return new CropArea{
                X = 0,
                Y = 0,
                Width = Math.Max(1, canvasWidth),
                Height = Math.Max(1, canvasHeight)
            };
        }

        const Int32 padding = 2;

        left =
            Math.Max(
                0,
                left - padding
            );

        top =
            Math.Max(
                0,
                top - padding
            );

        right =
            Math.Min(
                canvasWidth - 1,
                right + padding
            );

        bottom =
            Math.Min(
                canvasHeight - 1,
                bottom + padding
            );

        return new CropArea{
            X = left,
            Y = top,
            Width = right - left + 1,
            Height = bottom - top + 1
        };
    }

    internal BitmapImage DrawKey5(
        PluginImageSize imageSize,
        FocusTimerControlState state,
        Double progress)
    {
        var frame =
            Volatile.Read(ref this._key5Frame);

        var crop =
            Volatile.Read(ref this._key5Crop);

        if(frame == null || crop == null){
            return null;
        }

        var reveal =
            state is
                FocusTimerControlState.Running or
                FocusTimerControlState.Paused
                    ? Math.Clamp(
                        progress,
                        0.0,
                        1.0
                    )
                    : 0.0;

        return DrawImage(
            frame,
            crop,
            imageSize,
            reveal
        );
    }

    internal BitmapImage DrawKey6(
        PluginImageSize imageSize,
        FocusTimerControlState state)
    {
        var frame =
            Volatile.Read(ref this._key6Frame);

        var crop =
            Volatile.Read(ref this._key6Crop);

        if(frame == null || crop == null){
            return null;
        }

        var reveal =
            state is
                FocusTimerControlState.Ready or
                FocusTimerControlState.Completed
                    ? 1.0
                    : 0.0;

        return DrawImage(
            frame,
            crop,
            imageSize,
            reveal
        );
    }

    private static BitmapImage DrawImage(
        Byte[] bytes,
        CropArea crop,
        PluginImageSize imageSize,
        Double reveal)
    {
        using var dimensions =
            new BitmapBuilder(
                imageSize == PluginImageSize.None
                    ? PluginImageSize.Width90
                    : imageSize
            );

        using var target =
            new SKBitmap(
                dimensions.Width,
                dimensions.Height
            );

        using var canvas =
            new SKCanvas(target);

        canvas.Clear(
            new SKColor(24, 24, 26)
        );

        using var source =
            SKBitmap.Decode(bytes);

        if(source == null){
            return null;
        }

        var sourceArea =
            new SKRect(
                crop.X,
                crop.Y,
                crop.X + crop.Width,
                crop.Y + crop.Height
            );

        const Single margin = 2.0f;

        var availableWidth =
            target.Width -
            (margin * 2.0f);

        var availableHeight =
            target.Height -
            (margin * 2.0f);

        var scale =
            Math.Min(
                availableWidth /
                    sourceArea.Width,
                availableHeight /
                    sourceArea.Height
            );

        var width =
            sourceArea.Width * scale;

        var height =
            sourceArea.Height * scale;

        var x =
            (target.Width - width) /
            2.0f;

        var y =
            (target.Height - height) /
            2.0f;

        var destination =
            new SKRect(
                x,
                y,
                x + width,
                y + height
            );

        var grayscaleMatrix =
            new Single[]{
                0.2126f, 0.7152f, 0.0722f, 0, 0,
                0.2126f, 0.7152f, 0.0722f, 0, 0,
                0.2126f, 0.7152f, 0.0722f, 0, 0,
                0,       0,       0,       1, 0
            };

        using var grayscaleFilter =
            SKColorFilter.CreateColorMatrix(
                grayscaleMatrix
            );

        using var grayscalePaint =
            new SKPaint{
                FilterQuality =
                    SKFilterQuality.None,
                IsAntialias = false,
                ColorFilter =
                    grayscaleFilter
            };

        using var colorPaint =
            new SKPaint{
                FilterQuality =
                    SKFilterQuality.None,
                IsAntialias = false
            };

        canvas.DrawBitmap(
            source,
            sourceArea,
            destination,
            grayscalePaint
        );

        var colorRatio =
            (Single)Math.Clamp(
                reveal,
                0.0,
                1.0
            );

        if(colorRatio > 0.0f){
            var colorTop =
                destination.Bottom -
                (
                    destination.Height *
                    colorRatio
                );

            canvas.Save();

            canvas.ClipRect(
                new SKRect(
                    destination.Left,
                    colorTop,
                    destination.Right,
                    destination.Bottom
                )
            );

            canvas.DrawBitmap(
                source,
                sourceArea,
                destination,
                colorPaint
            );

            canvas.Restore();
        }

        using var image =
            SKImage.FromBitmap(target);

        using var data =
            image.Encode(
                SKEncodedImageFormat.Png,
                100
            );

        return BitmapImage.FromArray(
            data.ToArray()
        );
    }

    public void Dispose(){
        MainController.ModeChanged -=
            this.OnModeChanged;

        MainController.FocusTimerChanged -=
            this.OnFocusTimerChanged;

        this._stop.Cancel();

        _ = this._worker.ContinueWith(
            _ => this._stop.Dispose()
        );
    }

    private sealed class CropArea{
        public Int32 X {get; set;}
        public Int32 Y {get; set;}
        public Int32 Width {get; set;}
        public Int32 Height {get; set;}
    }

    private sealed class VisualSet{
        public Byte[][] Key5 {get; set;}
        public Byte[][] Key6 {get; set;}
        public CropArea Key5Crop {get; set;}
        public CropArea Key6Crop {get; set;}
        public Int32 Fps {get; set;}
    }
}
