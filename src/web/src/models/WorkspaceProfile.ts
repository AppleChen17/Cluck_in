export type WorkspaceProfile = {
  id: string;
  name: string;
  allowedApplications: string[];
  allowedWindowKeywords: string[];
  blockedApplications: string[];
  blockedWindowKeywords: string[];
};

export type RuleCategory = Exclude<keyof WorkspaceProfile, 'id' | 'name'>;
