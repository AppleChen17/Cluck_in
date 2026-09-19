import type { RuleCategory, WorkspaceProfile } from '../models/WorkspaceProfile.ts';

const categories: RuleCategory[] = [
  'allowedApplications', 'allowedWindowKeywords', 'blockedApplications', 'blockedWindowKeywords',
];

export function hasChanges(saved: WorkspaceProfile, draft: WorkspaceProfile): boolean {
  // Rules are unordered and case-insensitive in the agent.
  return categories.some(category => {
    const original = saved[category].map(value => value.toLowerCase()).sort();
    const current = draft[category].map(value => value.toLowerCase()).sort();
    return JSON.stringify(original) !== JSON.stringify(current);
  });
}

export function addRule(workspace: WorkspaceProfile, category: RuleCategory, input: string):
  { workspace: WorkspaceProfile; error?: string } {
  const value = input.trim();
  if (!value) return { workspace };
  if (workspace[category].some(rule => rule.toLowerCase() === value.toLowerCase())) {
    return { workspace, error: 'This rule is already in the list.' };
  }
  return { workspace: { ...workspace, [category]: [...workspace[category], value] } };
}

export function removeRule(workspace: WorkspaceProfile, category: RuleCategory, value: string): WorkspaceProfile {
  return { ...workspace, [category]: workspace[category].filter(rule => rule !== value) };
}
