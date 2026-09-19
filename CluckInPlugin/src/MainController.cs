namespace Loupedeck.CluckInPlugin;

public static class MainController{
    public static void HandleKeyEvent(int keyId){
        PluginLog.Info($"CluckIn received key {keyId}");
    }
}