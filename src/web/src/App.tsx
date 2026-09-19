import { useEffect, useState } from 'react';
import WorkspaceRules from './components/WorkspaceRules';
import TaskLauncher from './components/TaskLauncher';
import ChickenIntervention from './components/ChickenIntervention';
import './workspace-rules.css';

export default function App() {
  const [route, setRoute] = useState(window.location.hash);
  useEffect(() => {
    const update = () => setRoute(window.location.hash);
    window.addEventListener('hashchange', update);
    return () => window.removeEventListener('hashchange', update);
  }, []);
  const editingRules = route === '#/workspace-rules';
  return (
    <>
      <ChickenIntervention />
      <nav className="app-nav" aria-label="Main navigation">
        <a href="#/dashboard" aria-current={!editingRules ? 'page' : undefined}>Dashboard</a>
        <a href="#/workspace-rules" aria-current={editingRules ? 'page' : undefined}>Workspace Rules</a>
      </nav>
      <div hidden={editingRules}><Dashboard /></div>
      {/* Keep the editor mounted so tab navigation preserves per-workspace drafts. */}
      <div hidden={!editingRules}><WorkspaceRules /></div>
    </>
  );
}

const demo = {
  mode: 'Focus',
  task: 'Prepare Demo',
  timer: '24:32',
  message: {
    source: 'Slack',
    sender: 'Teammate',
    text: 'Can you help check the Logitech SDK issue?',
  },
  decision: 'SHOW_NOW',
  relevance: 0.92,
  urgency: 0.84,
  chicken: 'Focused',
  actions: [
    { time: '10:00', text: 'Entered Focus Mode' },
    { time: '10:01', text: 'Received Slack message' },
    { time: '10:01', text: 'AI classified message' },
  ],
};

function Dashboard() {
  const [currentTask, setCurrentTask] = useState(demo.task);
  return (
    <main className="dashboard">
      <header className="page-header">
        <div className="brand">
          <span className="brand-icon" aria-hidden="true">🐔</span>
          <div>
            <p className="eyebrow">YOUR FOCUS COMPANION</p>
            <h1>Cluck In Dashboard</h1>
          </div>
        </div>
        <div className="dashboard-header-actions">
          <span className="demo-badge">Mock data · Demo</span>
          <a className="workspace-rules-link" href="#/workspace-rules">Workspace Rules <span aria-hidden="true">→</span></a>
        </div>
      </header>

      <p className="intro">A little less distraction. A little more focus.</p>

      <div className="dashboard-grid">
        <TaskLauncher onStarted={setCurrentTask} />
        <section className="card session" aria-labelledby="session-heading">
          <div className="card-heading">
            <h2 id="session-heading">Session</h2>
            <span className="status"><span aria-hidden="true">●</span> In focus</span>
          </div>
          <p className="label">Current Mode</p>
          <ul className="modes" aria-label="Session modes">
            {['Idle', 'Focus', 'Auto'].map((mode) => (
              <li key={mode} className={mode === demo.mode ? 'mode active' : 'mode'}>
                {mode}{mode === demo.mode && <span className="sr-only"> (current)</span>}
              </li>
            ))}
          </ul>
          <p className="label">Current Task</p>
          <p className="task">{currentTask}</p>
          <div className="timer-block">
            <p className="label">Focus Timer</p>
            <p className="timer" aria-label="24 minutes and 32 seconds remaining">{demo.timer}</p>
            <p className="muted">remaining in this session</p>
          </div>
        </section>

        <section className="card message" aria-labelledby="message-heading">
          <div className="card-heading">
            <h2 id="message-heading">Incoming Message</h2>
            <span className="tag">1 message</span>
          </div>
          <p className="sender"><span className="source-icon" aria-hidden="true">#</span>{demo.message.source} <span className="muted">· {demo.message.sender}</span></p>
          <blockquote>“{demo.message.text}”</blockquote>
        </section>

        <section className="card decision" aria-labelledby="decision-heading">
          <div className="card-heading">
            <h2 id="decision-heading">AI Decision</h2>
            <span className="tag">Mock result</span>
          </div>
          <p className="decision-value">{demo.decision}</p>
          <p className="muted decision-note">This message is relevant to your current task.</p>
          <div className="scores">
            <div>
              <div className="score-label"><label htmlFor="relevance">Relevance score</label><strong>{demo.relevance.toFixed(2)}</strong></div>
              <meter id="relevance" min="0" max="1" value={demo.relevance}>{demo.relevance}</meter>
            </div>
            <div>
              <div className="score-label"><label htmlFor="urgency">Urgency score</label><strong>{demo.urgency.toFixed(2)}</strong></div>
              <meter id="urgency" min="0" max="1" value={demo.urgency}>{demo.urgency}</meter>
            </div>
          </div>
        </section>

        <section className="card chicken" aria-labelledby="chicken-heading">
          <h2 id="chicken-heading">Chicken</h2>
          <div className="chicken-portrait" aria-hidden="true">🐔</div>
          <p className="label">Chicken Status</p>
          <p className="chicken-status">{demo.chicken}</p>
          <p className="muted">Keeping an eye on what matters.</p>
        </section>

        <section className="card recent" aria-labelledby="recent-heading">
          <div className="card-heading">
            <h2 id="recent-heading">Recent Actions</h2>
            <span className="tag">Session activity</span>
          </div>
          <ol className="activity-list">
            {demo.actions.map((action) => (
              <li key={action.text}>
                <span className="activity-dot" aria-hidden="true" />
                <span>{action.text}</span>
                <time>{action.time}</time>
              </li>
            ))}
          </ol>
        </section>
      </div>
      <footer>Task buttons connect to Desktop Agent · Timer and AI results are demo data</footer>
    </main>
  );
}
