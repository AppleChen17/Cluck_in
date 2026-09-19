import type { WorkspaceProfile } from '../models/WorkspaceProfile.ts';

export interface WorkspaceService {
  getWorkspaces(): Promise<WorkspaceProfile[]>;
  getWorkspace(id: string): Promise<WorkspaceProfile>;
  updateWorkspace(id: string, workspace: WorkspaceProfile): Promise<WorkspaceProfile>;
}

const samples: WorkspaceProfile[] = [
  {
    id: 'coding', name: 'Coding',
    allowedApplications: ['code', 'WindowsTerminal'],
    allowedWindowKeywords: ['GitHub', 'Stack Overflow', 'Documentation'],
    blockedApplications: [],
    blockedWindowKeywords: ['YouTube', 'Netflix', 'Instagram'],
  },
  {
    id: 'meeting', name: 'Meeting',
    allowedApplications: ['ms-teams', 'Zoom'],
    allowedWindowKeywords: ['Google Meet', 'Microsoft Teams'],
    blockedApplications: [],
    blockedWindowKeywords: ['Instagram', 'Netflix'],
  },
  {
    id: 'reading', name: 'Reading',
    allowedApplications: ['Acrobat'],
    allowedWindowKeywords: ['Documentation', 'Wikipedia', '.pdf'],
    blockedApplications: [],
    blockedWindowKeywords: ['YouTube', 'Netflix', 'Instagram'],
  },
  {
    id: 'writing', name: 'Writing',
    allowedApplications: ['WINWORD', 'notepad'],
    allowedWindowKeywords: ['Google Docs'],
    blockedApplications: [],
    blockedWindowKeywords: ['YouTube', 'Netflix', 'Instagram'],
  },
];

// The component depends only on WorkspaceService. No C# endpoints exist yet.
// TODO: Replace this mock with an HTTP adapter:
// GET /api/workspaces -> WorkspaceProfile[]
// GET /api/workspaces/{id} -> WorkspaceProfile
// PUT /api/workspaces/{id} with the full profile -> saved WorkspaceProfile.
// Check response.ok, surface errors, and URL-encode IDs in that adapter.
export function createMockWorkspaceService(seed: WorkspaceProfile[] = samples): WorkspaceService {
  const profiles = new Map(seed.map(profile => [profile.id, structuredClone(profile)]));

  function find(id: string): WorkspaceProfile {
    const profile = profiles.get(id);
    if (!profile) throw new Error('Workspace not found: ' + id);
    return profile;
  }

  function clean(values: string[]): string[] {
    const seen = new Set<string>();
    return values.map(value => value.trim()).filter(value => {
      const key = value.toLowerCase();
      if (!value || seen.has(key)) return false;
      seen.add(key);
      return true;
    });
  }

  return {
    async getWorkspaces() {
      return structuredClone([...profiles.values()]);
    },
    async getWorkspace(id) {
      return structuredClone(find(id));
    },
    async updateWorkspace(id, workspace) {
      find(id);
      if (id !== workspace.id) throw new Error('Workspace ID must match the update target.');
      if (!workspace.name.trim()) throw new Error('Workspace name cannot be empty.');
      const saved: WorkspaceProfile = {
        id, name: workspace.name.trim(),
        allowedApplications: clean(workspace.allowedApplications),
        allowedWindowKeywords: clean(workspace.allowedWindowKeywords),
        blockedApplications: clean(workspace.blockedApplications),
        blockedWindowKeywords: clean(workspace.blockedWindowKeywords),
      };
      profiles.set(id, structuredClone(saved));
      return structuredClone(saved);
    },
  };
}

// Saved values last for this page session. Reloading resets to the sample profiles.
export const workspaceService: WorkspaceService = createMockWorkspaceService();
