namespace Loupedeck.CluckInPlugin;

using System;
using System.Collections.Generic;
using System.Threading;

public static class MainController{
    private const Int32 DefaultFocusDurationSeconds = 25 * 60;
    private const Int32 MaxFocusDurationSeconds = (24 * 60 * 60) - 1;
    private const Int32 CompletionFlashSteps = 6;

    private static readonly Object _focusTimerLock = new();

    private static readonly Timer _countdownRefreshTimer = new(
        _ => RefreshCountdownDisplay(),
        null,
        Timeout.Infinite,
        Timeout.Infinite
    );

    private static readonly Timer _completionFlashTimer = new(
        _ => RefreshCompletionFlash(),
        null,
        Timeout.Infinite,
        Timeout.Infinite
    );

    private static DateTimeOffset _runningSince;
    private static Int32 _remainingAtRunStartSeconds;
    private static Int32 _remainingFocusSeconds;
    private static Int32 _selectedFocusDurationSeconds =
        DefaultFocusDurationSeconds;
    private static Int32 _completionFlashStepsRemaining;

    public static CluckInMode CurrentMode {get; private set;} =
        CluckInMode.Focus;

    public static AIAssistMode CurrentAIAssistMode {get; private set;} =
        AIAssistMode.Off;

    public static FocusTimerControlState CurrentFocusTimerState {
        get;
        private set;
    } = FocusTimerControlState.Ready;

    public static FocusTimerField CurrentFocusTimerField {
        get;
        private set;
    } = FocusTimerField.Minutes;

    public static Int32 SelectedFocusDurationSeconds =>
        _selectedFocusDurationSeconds;

    public static Int32 SelectedFocusHours =>
        _selectedFocusDurationSeconds / 3600;

    public static Int32 SelectedFocusMinutes =>
        (_selectedFocusDurationSeconds % 3600) / 60;

    public static Int32 SelectedFocusSeconds =>
        _selectedFocusDurationSeconds % 60;

    public static Boolean FocusCompletionFlashOn {get; private set;}

    public static Int32 DisplayFocusDurationSeconds{
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
                        _selectedFocusDurationSeconds
                };
            }
        }
    }

    public static Int32 DisplayFocusHours =>
        DisplayFocusDurationSeconds / 3600;

    public static Int32 DisplayFocusMinutes =>
        (DisplayFocusDurationSeconds % 3600) / 60;

    public static Int32 DisplayFocusSeconds =>
        DisplayFocusDurationSeconds % 60;

    public static Double FocusProgress{
        get{
            lock(_focusTimerLock){
                if(_selectedFocusDurationSeconds <= 0){
                    return 0.0;
                }

                var remaining = CurrentFocusTimerState switch{
                    FocusTimerControlState.Running =>
                        CalculateRemainingSecondsNoLock(),
                    FocusTimerControlState.Paused =>
                        _remainingFocusSeconds,
                    FocusTimerControlState.Completed =>
                        0,
                    _ =>
                        _selectedFocusDurationSeconds
                };

                return Math.Clamp(
                    1.0 -
                    ((Double)remaining / _selectedFocusDurationSeconds),
                    0.0,
                    1.0
                );
            }
        }
    }

    public static Double GetFocusTimerSegmentRemainingRatio(
        Int32 segmentIndex)
    {
        if(segmentIndex < 0 || segmentIndex > 2){
            throw new ArgumentOutOfRangeException(
                nameof(segmentIndex)
            );
        }

        var totalSeconds =
            SelectedFocusDurationSeconds;

        if(totalSeconds <= 0){
            return 0.0;
        }

        var remainingFraction = Math.Clamp(
            (Double)DisplayFocusDurationSeconds /
            totalSeconds,
            0.0,
            1.0
        );

        var remainingAcrossThreeKeys =
            remainingFraction * 3.0;

        return Math.Clamp(
            remainingAcrossThreeKeys -
            (2 - segmentIndex),
            0.0,
            1.0
        );
    }
    public static event Action ModeChanged;
    public static event Action AIAssistModeChanged;
    public static event Action FocusTimerChanged;
    public static event Action<CluckInEvent> ActionRequested;

    public static void HandleKeyEvent(Int32 keyId){
        // Idle statistics keys are read-only; Focus dispatch below is unchanged.
        if(CurrentMode == CluckInMode.Idle && keyId is 4 or 6 or 7 or 8 or 9){
            return;
        }
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

    public static void AdjustFocusDuration(Int32 diff){
        if(diff == 0){
            return;
        }

        if(CurrentFocusTimerState is
            FocusTimerControlState.Running or
            FocusTimerControlState.Paused)
        {
            PluginLog.Info(
                "Focus duration change ignored while timer is active"
            );
            return;
        }

        StopCompletionFlash();

        if(CurrentFocusTimerState == FocusTimerControlState.Completed){
            CurrentFocusTimerState = FocusTimerControlState.Ready;
        }

        var secondsPerStep = CurrentFocusTimerField switch{
            FocusTimerField.Hours => 3600,
            FocusTimerField.Minutes => 60,
            FocusTimerField.Seconds => 1,
            _ => throw new ArgumentOutOfRangeException(
                nameof(CurrentFocusTimerField)
            )
        };

        var requested = (Int64)_selectedFocusDurationSeconds +
            ((Int64)diff * secondsPerStep);

        _selectedFocusDurationSeconds = (Int32)Math.Clamp(
            requested,
            0,
            MaxFocusDurationSeconds
        );

        PluginLog.Info(
            $"Focus duration changed to " +
            $"{SelectedFocusHours:00}:" +
            $"{SelectedFocusMinutes:00}:" +
            $"{SelectedFocusSeconds:00}"
        );

        FocusTimerChanged?.Invoke();
    }

    public static void ResetFocusDuration(){
        if(CurrentFocusTimerState is
            FocusTimerControlState.Running or
            FocusTimerControlState.Paused)
        {
            PluginLog.Info(
                "Focus duration reset ignored while timer is active"
            );
            return;
        }

        StopCompletionFlash();

        lock(_focusTimerLock){
            _selectedFocusDurationSeconds =
                DefaultFocusDurationSeconds;
            CurrentFocusTimerField = FocusTimerField.Minutes;
            CurrentFocusTimerState =
                FocusTimerControlState.Ready;
            _remainingFocusSeconds = 0;
        }

        PluginLog.Info(
            $"Focus duration reset to " +
            $"{SelectedFocusHours:00}:" +
            $"{SelectedFocusMinutes:00}:" +
            $"{SelectedFocusSeconds:00}"
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
            new Dictionary<String, Object>{
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
            CluckInAction.PetChicken
        );

        SendInputEvent(
            "PET_CHICKEN",
            new Dictionary<String, Object>(),
            2
        );
    }

    private static void HandleKey3(){
        if(CurrentMode == CluckInMode.Idle){
            RequestAction(
                3,
                CluckInAction.FeedChicken
            );

            SendInputEvent(
                "FEED_CHICKEN",
                new Dictionary<String, Object>(),
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
            AIAssistMode.Suggestion =>
                CluckInAction.AIAssistSuggestion,
            AIAssistMode.On => CluckInAction.AIAssistOn,
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode)
            )
        };

        RequestAction(3, action);

        var modeValue = mode switch{
            AIAssistMode.Off => "off",
            AIAssistMode.Suggestion => "suggestion",
            AIAssistMode.On => "on",
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode)
            )
        };

        SendInputEvent(
            "SET_AI_ASSIST_MODE",
            new Dictionary<String, Object>{
                ["mode"] = modeValue
            },
            3
        );

        AIAssistModeChanged?.Invoke();
    }

    private static void HandleKey4(){
        PluginLog.Info("Task selection opened");
    }

    private static void HandleFocusControlKey(){
        if(CurrentMode != CluckInMode.Focus){
            PluginLog.Info(
                "Focus timer control ignored outside Focus mode"
            );
            return;
        }

        switch(CurrentFocusTimerState){
            case FocusTimerControlState.Ready:
            case FocusTimerControlState.Completed:
                if(_selectedFocusDurationSeconds <= 0){
                    PluginLog.Info(
                        "Focus timer start ignored because duration is zero"
                    );
                    return;
                }

                SendInputEvent(
                    "START_FOCUS",
                    new Dictionary<String, Object>{
                        ["focusDurationSeconds"] =
                            _selectedFocusDurationSeconds
                    },
                    5
                );

                StartLocalCountdownMirror();
                break;

            case FocusTimerControlState.Running:
                SendInputEvent(
                    "PAUSE_FOCUS",
                    new Dictionary<String, Object>(),
                    5
                );

                PauseLocalCountdownMirror();
                break;

            case FocusTimerControlState.Paused:
                SendInputEvent(
                    "RESUME_FOCUS",
                    new Dictionary<String, Object>(),
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
            new Dictionary<String, Object>(),
            6
        );

        StopLocalCountdownMirror();
    }

    private static void StartLocalCountdownMirror(){
        StopCompletionFlash();

        lock(_focusTimerLock){
            _remainingFocusSeconds =
                _selectedFocusDurationSeconds;
            _remainingAtRunStartSeconds =
                _remainingFocusSeconds;
            _runningSince = DateTimeOffset.UtcNow;
            CurrentFocusTimerState =
                FocusTimerControlState.Running;

            _countdownRefreshTimer.Change(0, 250);
        }

        PluginLog.Info(
            "Local countdown display mirror started"
        );

        FocusTimerChanged?.Invoke();
    }

    private static void PauseLocalCountdownMirror(){
        lock(_focusTimerLock){
            _remainingFocusSeconds =
                CalculateRemainingSecondsNoLock();

            CurrentFocusTimerState =
                FocusTimerControlState.Paused;

            _countdownRefreshTimer.Change(
                Timeout.Infinite,
                Timeout.Infinite
            );
        }

        PluginLog.Info(
            "Local countdown display mirror paused"
        );

        FocusTimerChanged?.Invoke();
    }

    private static void ResumeLocalCountdownMirror(){
        lock(_focusTimerLock){
            _remainingAtRunStartSeconds =
                _remainingFocusSeconds;

            _runningSince = DateTimeOffset.UtcNow;

            CurrentFocusTimerState =
                FocusTimerControlState.Running;

            _countdownRefreshTimer.Change(0, 250);
        }

        PluginLog.Info(
            "Local countdown display mirror resumed"
        );

        FocusTimerChanged?.Invoke();
    }

    private static void StopLocalCountdownMirror(){
        lock(_focusTimerLock){
            _remainingFocusSeconds = 0;

            CurrentFocusTimerState =
                FocusTimerControlState.Ready;

            _countdownRefreshTimer.Change(
                Timeout.Infinite,
                Timeout.Infinite
            );
        }

        StopCompletionFlash();

        PluginLog.Info(
            "Local countdown display mirror stopped"
        );

        FocusTimerChanged?.Invoke();
    }

    private static void RefreshCountdownDisplay(){
        var completed = false;

        lock(_focusTimerLock){
            if(CurrentFocusTimerState !=
                FocusTimerControlState.Running)
            {
                return;
            }

            _remainingFocusSeconds =
                CalculateRemainingSecondsNoLock();

            if(_remainingFocusSeconds <= 0){
                _remainingFocusSeconds = 0;

                CurrentFocusTimerState =
                    FocusTimerControlState.Completed;

                _countdownRefreshTimer.Change(
                    Timeout.Infinite,
                    Timeout.Infinite
                );

                completed = true;
            }
        }

        if(completed){
            PluginLog.Info(
                "Local countdown display mirror completed"
            );

            StartCompletionFlash();
        }

        FocusTimerChanged?.Invoke();
    }

    private static Int32 CalculateRemainingSecondsNoLock(){
        if(CurrentFocusTimerState !=
            FocusTimerControlState.Running)
        {
            return _remainingFocusSeconds;
        }

        var elapsedSeconds = (Int32)Math.Floor(
            (DateTimeOffset.UtcNow - _runningSince)
                .TotalSeconds
        );

        return Math.Max(
            0,
            _remainingAtRunStartSeconds - elapsedSeconds
        );
    }

    private static void StartCompletionFlash(){
        lock(_focusTimerLock){
            FocusCompletionFlashOn = true;
            _completionFlashStepsRemaining =
                CompletionFlashSteps;

            _completionFlashTimer.Change(500, 500);
        }

        FocusTimerChanged?.Invoke();
    }

    private static void RefreshCompletionFlash(){
        var keepRunning = true;

        lock(_focusTimerLock){
            if(CurrentFocusTimerState !=
                FocusTimerControlState.Completed)
            {
                keepRunning = false;
            }
            else if(_completionFlashStepsRemaining <= 0){
                FocusCompletionFlashOn = false;
                CurrentFocusTimerState =
                    FocusTimerControlState.Ready;
                _remainingFocusSeconds = 0;
                keepRunning = false;
            }
            else{
                FocusCompletionFlashOn =
                    !FocusCompletionFlashOn;
                _completionFlashStepsRemaining--;
            }

            if(!keepRunning){
                _completionFlashTimer.Change(
                    Timeout.Infinite,
                    Timeout.Infinite
                );
            }
        }

        FocusTimerChanged?.Invoke();
    }

    private static void StopCompletionFlash(){
        lock(_focusTimerLock){
            FocusCompletionFlashOn = false;
            _completionFlashStepsRemaining = 0;

            _completionFlashTimer.Change(
                Timeout.Infinite,
                Timeout.Infinite
            );
        }
    }

    private static void RequestAction(
        Int32 keyId,
        CluckInAction action)
    {
        var request = new CluckInEvent{
            KeyId = keyId,
            Mode = CurrentMode,
            Action = action
        };

        PluginLog.Info(
            $"Action requested: key={request.KeyId}, " +
            $"mode={request.Mode}, action={request.Action}"
        );

        ActionRequested?.Invoke(request);
    }

    private static void SendInputEvent(
        String type,
        Dictionary<String, Object> payload,
        Int32 keyId)
    {
        var inputEvent = new InputEventRequest{
            Type = type,
            Payload = payload,
            Metadata = new Dictionary<String, Object>{
                ["keyId"] = keyId
            }
        };

        PluginLog.Info(
            $"InputEvent requested: type={type}"
        );

        _ = IntegrationClient.SendAsync(inputEvent);
    }
}
