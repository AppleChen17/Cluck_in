import assert from 'node:assert/strict';
import test from 'node:test';
import { createMockWorkspaceService } from '../src/services/workspaceService.ts';
import { addRule, removeRule, hasChanges } from '../src/components/workspaceRulesState.ts';

test('provides all four workspaces and returns isolated snapshots', async () => {
  const service = createMockWorkspaceService();
  const profiles = await service.getWorkspaces();
  assert.deepEqual(profiles.map(profile => profile.name), ['Coding', 'Meeting', 'Reading', 'Writing']);
  profiles[0].allowedApplications.push('accidental mutation');
  assert.equal((await service.getWorkspace('coding')).allowedApplications.includes('accidental mutation'), false);
});

test('every category supports trim, add, case-insensitive duplicates, empty values and removal', async () => {
  const original = await createMockWorkspaceService().getWorkspace('coding');
  for (const category of ['allowedApplications', 'allowedWindowKeywords', 'blockedApplications', 'blockedWindowKeywords']) {
    const added = addRule(original, category, '  New rule  ');
    assert.equal(added.workspace[category].at(-1), 'New rule');
    assert.equal(original[category].includes('New rule'), false);
    assert.equal(hasChanges(original, added.workspace), true);
    const duplicate = addRule(added.workspace, category, ' NEW RULE ');
    assert.ok(duplicate.error);
    assert.equal(duplicate.workspace, added.workspace);
    assert.equal(addRule(added.workspace, category, '   ').workspace, added.workspace);
    const removed = removeRule(added.workspace, category, 'New rule');
    assert.equal(hasChanges(original, removed), false);
  }
});

test('save persists within mock service, resets baseline and does not affect another workspace', async () => {
  const service = createMockWorkspaceService();
  const coding = await service.getWorkspace('coding');
  const meeting = await service.getWorkspace('meeting');
  const draft = addRule(coding, 'allowedApplications', 'rider').workspace;
  assert.equal((await service.getWorkspace('coding')).allowedApplications.includes('rider'), false);
  const saved = await service.updateWorkspace('coding', draft);
  assert.equal(hasChanges(saved, draft), false);
  assert.equal((await service.getWorkspace('coding')).allowedApplications.includes('rider'), true);
  assert.deepEqual(await service.getWorkspace('meeting'), meeting);
  saved.allowedApplications.push('mutated response');
  assert.equal((await service.getWorkspace('coding')).allowedApplications.includes('mutated response'), false);
  assert.equal((await createMockWorkspaceService().getWorkspace('coding')).allowedApplications.includes('rider'), false);
});

test('service rejects mismatched IDs and unknown workspaces without modifying stored profiles', async () => {
  const service = createMockWorkspaceService();
  const profile = await service.getWorkspace('coding');
  await assert.rejects(service.getWorkspace('missing'));
  await assert.rejects(service.updateWorkspace('meeting', profile));
  assert.deepEqual(await service.getWorkspace('coding'), profile);
});

test('service normalizes a full update and dirty tracking ignores rule order', async () => {
  const service = createMockWorkspaceService();
  const profile = await service.getWorkspace('coding');
  const saved = await service.updateWorkspace('coding', {
    ...profile, allowedApplications: [' code ', 'CODE', '', '  ', 'WindowsTerminal'],
  });
  assert.deepEqual(saved.allowedApplications, ['code', 'WindowsTerminal']);
  assert.equal(hasChanges(profile, { ...profile, allowedWindowKeywords: [...profile.allowedWindowKeywords].reverse() }), false);
});
