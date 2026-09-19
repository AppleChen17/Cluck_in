import { useEffect, useState } from 'react';
import { taskService, type TaskProfile } from '../services/taskService';
import './TaskLauncher.css';

export default function TaskLauncher({ onStarted }: { onStarted: (name: string) => void }) {
  const [tasks, setTasks] = useState<TaskProfile[]>([]);
  const [busy, setBusy] = useState(false);
  const [startingTaskId, setStartingTaskId] = useState<string | null>(null);
  const [activeTaskId, setActiveTaskId] = useState<string | null>(null);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');

  async function load() {
    setBusy(true);
    setError('');
    try { setTasks(await taskService.getTasks()); }
    catch (e) { setError(e instanceof Error ? e.message : '無法載入 Tasks。'); }
    finally { setBusy(false); }
  }

  useEffect(() => { void load(); }, []);

  async function start(task: TaskProfile) {
    setBusy(true);
    setStartingTaskId(task.id);
    setError('');
    setMessage('');
    try {
      const result = await taskService.startTask(task.id);
      onStarted(result.taskName);
      setActiveTaskId(result.taskId);
      setMessage(`已啟動 ${result.taskName}。`);
    } catch (e) { setError(e instanceof Error ? e.message : 'Task 啟動失敗。'); }
    finally { setBusy(false); setStartingTaskId(null); }
  }

  return <section id="tasks" tabIndex={-1} className="card tasks-panel" aria-labelledby="tasks-heading">
    <div className="card-heading tasks-heading">
      <h2 id="tasks-heading">Tasks</h2>
      <button className="tasks-refresh" type="button" disabled={busy}
        onClick={() => void load()} aria-label="重新載入 Tasks">
        <span aria-hidden="true">↻</span>
        {busy && startingTaskId === null ? 'Refreshing…' : 'Refresh'}
      </button>
    </div>
    <div className="tasks-list" aria-busy={busy}>
      {tasks.map(task => <article key={task.id}
        className={`task-card${activeTaskId === task.id ? ' task-card-active' : ''}`}>
        <div className="task-card-heading">
          <h3 className="task-card-name">{task.name}</h3>
          {activeTaskId === task.id && <span className="task-active-badge"
            title="Last successfully started from this dashboard">ACTIVE</span>}
        </div>
        {task.description && <p className="task-card-description">{task.description}</p>}
        <ul className="task-card-meta" aria-label="Task details">
          {task.focusDurationMinutes != null && <li>{task.focusDurationMinutes} min focus</li>}
          <li>{task.apps.length} {task.apps.length === 1 ? 'app' : 'apps'}</li>
          <li>{task.urls.length} {task.urls.length === 1 ? 'website' : 'websites'}</li>
        </ul>
        <div className="task-card-actions">
          <button className="task-start" type="button" disabled={busy}
            onClick={() => void start(task)} aria-label={`${startingTaskId === task.id ? 'Starting' : 'Start Task'}: ${task.name}`}>
            {startingTaskId === task.id
              ? <><span className="task-start-spinner" aria-hidden="true" />Starting...</>
              : <>Start Task <span aria-hidden="true">→</span></>}
          </button>
        </div>
      </article>)}
    </div>
    {busy && <p className="tasks-feedback" role="status">{startingTaskId ? '正在啟動工作環境…' : '正在載入 Tasks…'}</p>}
    {!busy && !error && tasks.length === 0 && <p className="tasks-feedback">No tasks yet. Refresh when your tasks are ready.</p>}
    {message && <p className="tasks-feedback tasks-feedback-success" role="status">{message}</p>}
    {error && <p className="tasks-feedback tasks-feedback-error" role="alert">{error}</p>}
  </section>;
}
