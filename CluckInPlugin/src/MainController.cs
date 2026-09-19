namespace Loupedeck.CluckInPlugin;

using System;
using System.Collections.Generic;

public static class MainController{
    public static CluckInMode CurrentMode { get; private set; } = CluckInMode.Focus;

    public static AIAssistMode CurrentAIAssistMode { get; private set; } =
        AIAssistMode.Off;

    public static event Action ModeChanged;
    public static event Action AIAssistModeChanged;
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

            default:
                PluginLog.Info($"No action assigned to key {keyId}");
                break;
        }
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