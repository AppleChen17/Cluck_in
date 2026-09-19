using CluckIn.App.Interfaces;
using CluckIn.App.Models;

namespace CluckIn.App.Orchestration;

public sealed class InMemoryFocusMessageStore : IFocusMessageStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, FocusMessageRecord>>
        _sessions = new(StringComparer.Ordinal);

    public void CreateSession(string focusSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(focusSessionId);
        lock(_gate){
            if(_sessions.ContainsKey(focusSessionId)){
                return;
            }

            _sessions[focusSessionId] =
                new Dictionary<string, FocusMessageRecord>(StringComparer.Ordinal);
        }
    }

    public void Add(string focusSessionId, FocusMessageRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(focusSessionId);
        ArgumentNullException.ThrowIfNull(record);

        lock(_gate){
            if(!_sessions.TryGetValue(focusSessionId, out var messages)){
                return;
            }

            messages[record.Message.Id] = record;
        }
    }

    public IReadOnlyList<FocusMessageRecord> Snapshot(string focusSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(focusSessionId);
        lock(_gate){
            if(!_sessions.TryGetValue(focusSessionId, out var messages)){
                return [];
            }

            // Dictionary insertion order follows serialized intake, not provider timestamps.
            return messages.Values.ToArray();
        }
    }

    public void FreeSession(string focusSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(focusSessionId);
        lock(_gate){
            _sessions.Remove(focusSessionId);
        }
    }
}
