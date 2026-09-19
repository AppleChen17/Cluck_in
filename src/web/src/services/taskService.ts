export interface TaskProfile {
  id: string;
  name: string;
  description?: string;
  apps: string[];
  urls: string[];
  allowedApps: string[];
  allowedDomains: string[];
  focusDurationMinutes?: number;
}

async function request<T>(path: string, options?: RequestInit): Promise<T> {
  const response = await fetch(path, options);
  const body = await response.json().catch(() => null);
  if (!response.ok || !body) {
    throw new Error(body?.error ?? '無法連線到 Desktop Agent，請確認 Cluck In 已啟動。');
  }
  return body as T;
}

export const taskService = {
  getTasks: () => request<TaskProfile[]>('/api/tasks'),
  startTask: (taskId: string) => request<{ success: boolean; taskId: string; taskName: string }>(
    `/api/tasks/${encodeURIComponent(taskId)}/start`,
    { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' },
  ),
};
