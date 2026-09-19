namespace Loupedeck.CluckInPlugin;

using System;
using System.Collections.Generic;
using System.Threading;

public static class MainController{
    private const int DefaultFocusHours = 0;
    private const int DefaultFocusMinutes = 25;
    private const int DefaultFocusSeconds = 0;

    private static readonly object _focusTimerLock = new();

    private static readonly Timer _countdownRefreshTimer = new(
        _ => RefreshCountdownDisplay(),
        null,
        Timeout.Infinite,
        Timeout.Infinite
    );

    private static DateTimeOffset _runningSince;
    private static int _remainingAtRunStartSeconds;
    private static int _remainingFocusSeconds;

    public static CluckInMode CurrentMode {get; private set;} = CluckInMode.Focus;

    public static AIAssistMode CurrentAIAssistMode {get; private set;} =
        AIAssistMode.Off;

    public static FocusTimerControlState CurrentFocusTimerState {get; private set;} =
        FocusTimerControlState.Ready;

    public static FocusTimerField CurrentFocusTimerField {get; private set;} =
        FocusTimerField.Minutes;

    public static int SelectedFocusHours {get; private set;} =
        DefaultFocusHours;

    public static int SelectedFocusMinutes {get; private set;} =
        DefaultFocusMinutes;

    public static int SelectedFocusSeconds {get; private set;} =
        DefaultFocusSeconds;

    public static int SelectedFocusDurationSeconds =>
        (SelectedFocusHours * 3600) +
        (SelectedFocusMinutes * 60) +
        SelectedFocusSeconds;

    public static int DisplayFocusDurationSeconds{
        get{
            lock(_focusTimerLock){
                return CurrentFocusTimerState switch{
                    FocusTimerControlState.Running =>
                        CalculateRemainingSecondsNoLock(),
                    FocusTimerControlState.Paused =>
                        _remainingFocusSeconds,
                    FocusTimerControlState.Completed =>
                        0,
                    _ =>
                        SelectedFocusDurationSeconds
                };
            }
        }
    }

    public static int DisplayFocusHours =>
        DisplayFocusDurationSeconds / 3600;

    public static int DisplayFocusMinutes =>
        (DisplayFocusDurationSeconds % 3600) / 60;

    public static int DisplayFocusSeconds =>
        DisplayFocusDurationSeconds % 60;

    public static event Action ModeChanged;
    public static event Action AIAssistModeChanged;
    public static event Action FocusTimerChanged;
    public static event Action<CluckInEvent> ActionRequested;

    public static void HandleKeyEvent(int keyId){
        PluginLog.Info($"CluckIn received key {keyId}");

        switch(keyId){
            case 1:
                HandleModeKey();
                break;

            case 2:
                HandleKey2();
                break;

            case 3:
                HandleKey3();
                break;

            case 4:
                HandleKey4();
                break;

            case 5:
                HandleFocusControlKey();
                break;

            case 6:
                HandleFocusStopKey();
                break;

            case 7:
                SelectFocusTimerField(FocusTimerField.Hours);
                break;

            case 8:
                SelectFocusTimerField(FocusTimerField.Minutes);
                break;

            case 9:
                SelectFocusTimerField(FocusTimerField.Seconds);
                break;

            default:
                PluginLog.Info($"No action assigned to key {keyId}");
                break;
        }
    }

    public static void SelectFocusTimerField(FocusTimerField field){
        CurrentFocusTimerField = field;

        PluginLog.Info($"Focus timer field selected: {field}");

        FocusTimerChanged?.Invoke();
    }

    public static void AdjustFocusDuration(int diff){
        if(diff == 0){
            return;
        }

        if(CurrentFocusTimerState is
            FocusTimerControlState.Running or
            FocusTimerControlState.Paused)
        {
            PluginLog.Info("Focus duration change ignored while timer is active");
            return;
        }

        if(CurrentFocusTimerState == FocusTimerControlState.Completed){
            CurrentFocusTimerState = FocusTimerControlState.Ready;
        }

        switch(CurrentFocusTimerField){
            case FocusTimerField.Hours:
                SelectedFocusHours = Math.Clamp(
                    SelectedFocusHours + diff,
                    0,
                    23
                );
                break;

            case FocusTimerField.Minutes:
                SelectedFocusMinutes = Math.Clamp(
                    SelectedFocusMinutes + diff,
                    0,
                    59
                );
                break;

            case FocusTimerField.Seconds:
                SelectedFocusSeconds = Math.Clamp(
                    SelectedFocusSeconds + diff,
                    0,
                    59
                );
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(CurrentFocusTimerField)
                );
        }

        PluginLog.Info(
            $"Focus duration changed to {SelectedFocusHours:00}:{SelectedFocusMinutes:00}:{SelectedFocusSeconds:00}"
        );

        FocusTimerChanged?.Invoke();
    }

    public static void ResetFocusDuration(){
        if(CurrentFocusTimerState is
            FocusTimerControlState.Running or
            FocusTimerControlState.Paused)
        {
            PluginLog.Info("Focus duration reset ignored while timer is active");
            return;
        }

        lock(_focusTimerLock){
            SelectedFocusHours = DefaultFocusHours;
            SelectedFocusMinutes = DefaultFocusMinutes;
            SelectedFocusSeconds = DefaultFocusSeconds;
            CurrentFocusTimerField = FocusTimerField.Minutes;
            CurrentFocusTimerState = FocusTimerControlState.Ready;
            _remainingFocusSeconds = 0;
        }

        PluginLog.Info(
            $"Focus duration reset to {SelectedFocusHours:00}:{SelectedFocusMinutes:00}:{SelectedFocusSeconds:00}"
        );

        FocusTimerChanged?.Invoke();
    }

    private static void HandleModeKey(){
        if(CurrentMode == CluckInMode.Idle){
            SetMode(CluckInMode.Focus);
            return;
        }

        if(CurrentMode == CluckInMode.Focus){
            SetMode(CluckInMode.Idle);
        }
    }

    private static void SetMode(CluckInMode mode){
        CurrentMode = mode;

        PluginLog.Info($"Mode changed to {CurrentMode}");

        var modeValue =
            mode == CluckInMode.Focus
                ? "focus"
                : "idle";

        SendInputEvent(
            "CHANGE_MODE",
            new Dictionary<string, object>{
                ["mode"] = modeValue
            },
            1
        );

        ModeChanged?.Invoke();
    }

    private static void HandleKey2(){
        if(CurrentMode == CluckInMode.Focus){
            RequestAction(
                2,
                CluckInAction.ViewMessages
            );

            PluginLog.Info(
                "SHOW_MESSAGES integration pending shared contract"
            );

            return;
        }

        RequestAction(
            2,
            CluckInAction.FeedChicken
        );

        SendInputEvent(
            "FEED_CHICKEN",
            new Dictionary<string, object>(),
            2
        );
    }

    private static void HandleKey3(){
        if(CurrentMode == CluckInMode.Idle){
            RequestAction(
                3,
                CluckInAction.PetChicken
            );

            SendInputEvent(
                "PET_CHICKEN",
                new Dictionary<string, object>(),
                3
            );

            return;
        }

        if(CurrentAIAssistMode == AIAssistMode.Off){
            SetAIAssistMode(AIAssistMode.Suggestion);
            return;
        }

        if(CurrentAIAssistMode == AIAssistMode.Suggestion){
            SetAIAssistMode(AIAssistMode.On);
            return;
        }

        if(CurrentAIAssistMode == AIAssistMode.On){
            SetAIAssistMode(AIAssistMode.Off);
        }
    }

    private static void SetAIAssistMode(AIAssistMode mode){
        CurrentAIAssistMode = mode;

        PluginLog.Info($"AI assist mode changed to {mode}");

        var action = mode switch{
            AIAssistMode.Off => CluckInAction.AIAssistOff,
            AIAssistMode.Suggestion => CluckInAction.AIAssistSuggestion,
            AIAssistMode.On => CluckInAction.AIAssistOn,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        RequestAction(3, action);

        var eventType = mode switch{
            AIAssistMode.Off => "AI_ASSIST_OFF",
            AIAssistMode.Suggestion => "AI_ASSIST_SUGGESTION",
            AIAssistMode.On => "AI_ASSIST_ON",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        SendInputEvent(
            eventType,
            new Dictionary<string, object>(),
            3
        );

        AIAssistModeChanged?.Invoke();
    }

    private static void HandleKey4(){
        PluginLog.Info("Task selection opened");
    }

    private static void HandleFocusControlKey(){
        if(CurrentMode != CluckInMode.Focus){
            PluginLog.Info("Focus timer control ignored outside Focus mode");
            return;
        }

        switch(CurrentFocusTimerState){
            case FocusTimerControlState.Ready:
            case FocusTimerControlState.Completed:
                if(SelectedFocusDurationSeconds <= 0){
                    PluginLog.Info("Focus timer start ignored because duration is zero");
                    return;
                }

                SendInputEvent(
                    "START_FOCUS",
                    new Dictionary<string, object>{
                        ["focusDurationSeconds"] =
                            SelectedFocusDurationSeconds
                    },
                    5
                );

                StartLocalCountdownMirror();
                break;

            case FocusTimerControlState.Running:
                SendInputEvent(
                    "PAUSE_FOCUS",
                    new Dictionary<string, object>(),
                    5
                );

                PauseLocalCountdownMirror();
                break;

            case FocusTimerControlState.Paused:
                SendInputEvent(
                    "RESUME_FOCUS",
                    new Dictionary<string, object>(),
                    5
                );

                ResumeLocalCountdownMirror();
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(CurrentFocusTimerState)
                );
        }
    }

    private static void HandleFocusStopKey(){
        if(CurrentFocusTimerState is
            FocusTimerControlState.Ready or
            FocusTimerControlState.Completed)
        {
            PluginLog.Info("Focus timer is not active");
            return;
        }

        SendInputEvent(
            "STOP_FOCUS",
            new Dictionary<string, object>(),
            6
        );

        StopLocalCountdownMirror();
    }

    private static void StartLocalCountdownMirror(){
        lock(_focusTimerLock){
            _remainingFocusSeconds = SelectedFocusDurationSeconds;
            _remainingAtRunStartSeconds = _remainingFocusSeconds;
            _runningSince = DateTimeOffset.UtcNow;
            CurrentFocusTimerState = FocusTimerControlState.Running;
            _countdownRefreshTimer.Change(0, 250);
        }

        PluginLog.Info("Local countdown display mirror started");

        FocusTimerChanged?.Invoke();
    }

    private static void PauseLocalCountdownMirror(){
        lock(_focusTimerLock){
            _remainingFocusSeconds = CalculateRemainingSecondsNoLock();
            CurrentFocusTimerState = FocusTimerControlState.Paused;
            _countdownRefreshTimer.Change(
                Timeout.Infinite,
                Timeout.Infinite
            );
        }

        PluginLog.Info("Local countdown display mirror paused");

        FocusTimerChanged?.Invoke();
    }

    private static void ResumeLocalCountdownMirror(){
        lock(_focusTimerLock){
            _remainingAtRunStartSeconds = _remainingFocusSeconds;
            _runningSince = DateTimeOffset.UtcNow;
            CurrentFocusTimerState = FocusTimerControlState.Running;
            _countdownRefreshTimer.Change(0, 250);
        }

        PluginLog.Info("Local countdown display mirror resumed");

        FocusTimerChanged?.Invoke();
    }

    private static void StopLocalCountdownMirror(){
        lock(_focusTimerLock){
            _remainingFocusSeconds = 0;
            CurrentFocusTimerState = FocusTimerControlState.Ready;
            _countdownRefreshTimer.Change(
                Timeout.Infinite,
                Timeout.Infinite
            );
        }

        PluginLog.Info("Local countdown display mirror stopped");

        FocusTimerChanged?.Invoke();
    }

    private static void RefreshCountdownDisplay(){
        var completed = false;

        lock(_focusTimerLock){
            if(CurrentFocusTimerState != FocusTimerControlState.Running){
                return;
            }

            _remainingFocusSeconds = CalculateRemainingSecondsNoLock();

            if(_remainingFocusSeconds <= 0){
                _remainingFocusSeconds = 0;
                CurrentFocusTimerState = FocusTimerControlState.Completed;
                _countdownRefreshTimer.Change(
                    Timeout.Infinite,
                    Timeout.Infinite
                );
                completed = true;
            }
        }

        if(completed){
            PluginLog.Info("Local countdown display mirror completed");
        }

        FocusTimerChanged?.Invoke();
    }

    private static int CalculateRemainingSecondsNoLock(){
        if(CurrentFocusTimerState != FocusTimerControlState.Running){
            return _remainingFocusSeconds;
        }

        var elapsedSeconds = (int)Math.Floor(
            (DateTimeOffset.UtcNow - _runningSince).TotalSeconds
        );

        return Math.Max(
            0,
            _remainingAtRunStartSeconds - elapsedSeconds
        );
    }

    private static void RequestAction(
        int keyId,
        CluckInAction action)
    {
        var request = new CluckInEvent{
            KeyId = keyId,
            Mode = CurrentMode,
            Action = action
        };

        PluginLog.Info(
            $"Action requested: key={request.KeyId}, mode={request.Mode}, action={request.Action}"
        );

        ActionRequested?.Invoke(request);
    }

    private static void SendInputEvent(
        string type,
        Dictionary<string, object> payload,
        int keyId)
    {
        var inputEvent = new InputEventRequest{
            Type = type,
            Payload = payload,
            Metadata = new Dictionary<string, object>{
                ["keyId"] = keyId
            }
        };

        PluginLog.Info(
            $"InputEvent requested: type={type}"
        );

        _ = IntegrationClient.SendAsync(inputEvent);
    }
}
