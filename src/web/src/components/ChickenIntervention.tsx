import { useEffect, useRef, useState } from 'react';
import { interventionService, type InterventionState } from '../services/interventionService';
import './ChickenIntervention.css';

export default function ChickenIntervention() {
  const [state, setState] = useState<InterventionState | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const acting = useRef(false);
  const version = useRef(0);
  useEffect(() => {
    let disposed = false;
    let timer: ReturnType<typeof setTimeout>;
    const poll = async () => {
      const currentVersion = version.current;
      try {
        const next = await interventionService.getState();
        if (!disposed && !acting.current && currentVersion === version.current) setState(next);
      } catch {
        if (!disposed && !acting.current) {
          setState(null);
          setError('Chicken Intervention disconnected. Check Desktop Agent.');
        }
      } finally {
        if (!disposed) timer = setTimeout(poll, 1000);
      }
    };
    void poll();
    return () => { disposed = true; clearTimeout(timer); };
  }, []);

  async function act(action: 2 | 3) {
    if (!state?.id || acting.current) return;
    acting.current = true;
    version.current++;
    setBusy(true);
    setError('');
    try { setState(await interventionService.act(state.id, action)); }
    catch (e) { setError(e instanceof Error ? e.message : 'Action failed. Please retry.'); }
    finally { acting.current = false; setBusy(false); }
  }

  if (!state?.isActive) return null;
  return (
    <aside className="chicken-intervention" role="region" aria-label="Chicken Intervention" aria-live="polite">
      <h2>🐔 Back to your task?</h2>
      <p>{state.reason}</p>
      <p><strong>{state.currentDomain ?? state.currentApp}</strong></p>
      <div className="chicken-intervention-actions">
        <button disabled={busy || !state.canReturnToWork} onClick={() => void act(2)}>Back to Work</button>
        <button disabled={busy} onClick={() => void act(3)}>Allow Temporarily · 5 min</button>
      </div>
      {!state.canReturnToWork && <p>Open an allowed app to return to work.</p>}
      {error && <p role="alert">{error}</p>}
    </aside>
  );
}
