namespace Loupedeck.CluckInPlugin;

using System;
using System.Collections.Generic;

public static class MainController{
    private const int DefaultFocusHours = 0;
    private const int DefaultFocusMinutes = 25;
    private const int DefaultFocusSeconds = 0;

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

        if(CurrentFocusTimerState != FocusTimerControlState.Ready){
            PluginLog.Info("Focus duration change ignored while timer is active");
            return;
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
        if(CurrentFocusTimerState != FocusTimerControlState.Ready){
            PluginLog.Info("Focus duration reset ignored while timer is active");
            return;
        }

        SelectedFocusHours = DefaultFocusHours;
        SelectedFocusMinutes = DefaultFocusMinutes;
        SelectedFocusSeconds = DefaultFocusSeconds;
        CurrentFocusTimerField = FocusTimerField.Minutes;

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
        RequestAction(4, CluckInAction.SelectTask);
        SendInputEvent("SHOW_TASK_SELECTION", new Dictionary<string, object>(), 4);
    }

    public static void SelectTask(string taskId){
        if(string.IsNullOrWhiteSpace(taskId)){
            return;
        }

        SendInputEvent(
            "SELECT_TASK",
            new Dictionary<string, object>{ ["taskId"] = taskId },
            4
        );
    }

    private static void HandleFocusControlKey(){
        if(CurrentMode != CluckInMode.Focus){
            PluginLog.Info("Focus timer control ignored outside Focus mode");
            return;
        }

        switch(CurrentFocusTimerState){
            case FocusTimerControlState.Ready:
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

                SetFocusTimerState(FocusTimerControlState.Running);
                break;

            case FocusTimerControlState.Running:
                SendInputEvent(
                    "PAUSE_FOCUS",
                    new Dictionary<string, object>(),
                    5
                );

                SetFocusTimerState(FocusTimerControlState.Paused);
                break;

            case FocusTimerControlState.Paused:
                SendInputEvent(
                    "RESUME_FOCUS",
                    new Dictionary<string, object>(),
                    5
                );

                SetFocusTimerState(FocusTimerControlState.Running);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(CurrentFocusTimerState)
                );
        }
    }

    private static void HandleFocusStopKey(){
        if(CurrentFocusTimerState == FocusTimerControlState.Ready){
            PluginLog.Info("Focus timer is not active");
            return;
        }

        SendInputEvent(
            "STOP_FOCUS",
            new Dictionary<string, object>(),
            6
        );

        SetFocusTimerState(FocusTimerControlState.Ready);
    }

    private static void SetFocusTimerState(FocusTimerControlState state){
        CurrentFocusTimerState = state;

        PluginLog.Info($"Focus timer control state changed to {state}");

        FocusTimerChanged?.Invoke();
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
