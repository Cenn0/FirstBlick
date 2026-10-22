import { describe, expect, it } from 'vitest';
import { DependencyGraph, blockedBy, validDependencyChange, validDependencyGraph, wouldCreateTaskCycle } from '../src/app/core/task-dependencies.models';

const project = '91000000-0000-4000-8000-000000000001', a = '91000000-0000-4000-8000-000000000002', b = '91000000-0000-4000-8000-000000000003', c = '91000000-0000-4000-8000-000000000004';
const graph = (): DependencyGraph => ({ tenantId: 'tenant-a', projectId: project, projectName: 'Dachsanierung', tasks: [
  { id: a, revision: 1, title: 'Material prüfen', status: 'Done', archived: false, predecessorTaskIds: [] },
  { id: b, revision: 2, title: 'Dach schließen', status: 'Open', archived: false, predecessorTaskIds: [a] },
  { id: c, revision: 3, title: 'Abnahme', status: 'Open', archived: false, predecessorTaskIds: [b] }
] });
describe('task dependency boundary', () => {
  it('accepts a consistent graph and derives unfinished predecessors', () => {
    const value = validDependencyGraph(graph(), 'tenant-a', project);
    expect(blockedBy(value, value.tasks[1])).toEqual([]);
    expect(blockedBy(value, value.tasks[2]).map(task => task.id)).toEqual([b]);
    expect(wouldCreateTaskCycle(value, a, c)).toBe(true); expect(wouldCreateTaskCycle(value, c, a)).toBe(false);
  });
  it('rejects foreign tenants and projects', () => {
    expect(() => validDependencyGraph(graph(), 'tenant-b', project)).toThrow();
    expect(() => validDependencyGraph(graph(), 'tenant-a', a)).toThrow();
  });
  it('rejects duplicate task identities, missing nodes, self links and cycles', () => {
    const duplicate = graph(); duplicate.tasks.push(duplicate.tasks[0]); expect(() => validDependencyGraph(duplicate, 'tenant-a', project)).toThrow();
    const missing = graph(); missing.tasks.shift(); expect(() => validDependencyGraph(missing, 'tenant-a', project)).toThrow();
    const self = graph(); self.tasks[2].predecessorTaskIds = [c]; expect(() => validDependencyGraph(self, 'tenant-a', project)).toThrow();
    const cycle = graph(); cycle.tasks[0].status = 'Open'; cycle.tasks[0].predecessorTaskIds = [c]; expect(() => validDependencyGraph(cycle, 'tenant-a', project)).toThrow();
  });
  it('rejects archived predecessors and prematurely completed work', () => {
    const archived = graph(); archived.tasks[0].archived = true; expect(() => validDependencyGraph(archived, 'tenant-a', project)).toThrow();
    const premature = graph(); premature.tasks[2].status = 'Done'; expect(() => validDependencyGraph(premature, 'tenant-a', project)).toThrow();
  });
  it.each([0, 1.5, Number.MAX_SAFE_INTEGER + 1])('rejects unusable revisions %s', revision => {
    const value = graph(); value.tasks[0].revision = revision; expect(() => validDependencyGraph(value, 'tenant-a', project)).toThrow();
  });
  it('rejects unknown status and overlong title', () => {
    const value = graph(); (value.tasks[0] as any).status = 'Ready'; expect(() => validDependencyGraph(value, 'tenant-a', project)).toThrow();
    value.tasks[0].status = 'Done'; value.tasks[0].title = 'x'.repeat(241); expect(() => validDependencyGraph(value, 'tenant-a', project)).toThrow();
  });
  it('accepts a revision-bound exact dependency or status change', () => {
    const before = graph().tasks[1];
    expect(validDependencyChange({ ...before, revision: 3, predecessorTaskIds: [] }, before, { predecessorTaskIds: [] }).revision).toBe(3);
    expect(validDependencyChange({ ...before, revision: 3, status: 'Done' }, before, { status: 'Done' }).status).toBe('Done');
  });
  it('does not treat an unrelated response or revision as a saved request', () => {
    const before = graph().tasks[1];
    for (const changed of [{ id: a }, { title: 'Andere Aufgabe' }, { revision: 4 }, { archived: true }, { status: 'Cancelled' }, { predecessorTaskIds: [] }])
      expect(() => validDependencyChange({ ...before, revision: 3, status: 'Done', ...changed }, before, { status: 'Done' })).toThrow();
  });
  it('rejects unbounded and duplicate predecessor lists', () => {
    const value = graph(); value.tasks[1].predecessorTaskIds = Array(17).fill(a); expect(() => validDependencyGraph(value, 'tenant-a', project)).toThrow();
    value.tasks[1].predecessorTaskIds = [a, a]; expect(() => validDependencyGraph(value, 'tenant-a', project)).toThrow();
    value.tasks = Array(1001).fill(value.tasks[0]); expect(() => validDependencyGraph(value, 'tenant-a', project)).toThrow();
  });
});
