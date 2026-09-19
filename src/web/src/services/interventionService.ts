export interface InterventionState {
  id: string | null;
  isActive: boolean;
  severity: number;
  reason: string;
  currentApp: string | null;
  currentDomain: string | null;
  triggeredAt: string | null;
  canReturnToWork: boolean;
}

async function request(options?: RequestInit): Promise<InterventionState> {
  const response = await fetch(`/api/intervention${options ? '/action' : ''}`, options);
  const body = await response.json();
  if (!response.ok) throw new Error(body.error ?? 'Desktop Agent unavailable.');
  return body;
}

export const interventionService = {
  getState: () => request(),
  act: (interventionId: string, action: 2 | 3) => request({
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ interventionId, action }),
  }),
};
