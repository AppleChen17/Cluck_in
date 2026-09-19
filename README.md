# Cluck_in

2026 MeiChu Hackathon project using Logitech MX Creative Console and Logitech Actions SDK.

## Current Status

Current development branch:

`feature/main-controller`

### Completed

- [x] Install Logi Options+
- [x] Install Logitech Plugin Tool
- [x] Generate `CluckInPlugin`
- [x] Configure project for .NET 10
- [x] Build Logitech plugin successfully
- [x] Load `CluckIn` in Logi Options+
- [x] Trigger a command from a physical MX Creative Console key
- [x] Add `MainController`
- [x] Route physical key event to `MainController`

Verified event chain:

```text
MX Creative Console
→ Logi Options+
→ CounterCommand.RunCommand()
→ MainController.HandleKeyEvent(1)
→ PluginLog