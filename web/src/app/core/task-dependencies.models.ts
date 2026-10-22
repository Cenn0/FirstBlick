import { recordId, recordObject, recordRevision, recordText } from './ledger-contracts';

export const taskStatuses = { Open: 'Offen', InProgress: 'In Bearbeitung', Done: 'Erledigt', Cancelled: 'Entfallen' } as const;
export type DependencyTaskStatus = keyof typeof taskStatuses;
export interface DependencyTask { id: string; revision: number; title: string; status: DependencyTaskStatus; archived: boolean; predecessorTaskIds: string[] }
export interface DependencyGraph { tenantId: string; projectId: string; projectName: string; tasks: DependencyTask[] }
const fail = (): never => { throw new Error('Die Aufgabenabhängigkeiten sind unvollständig oder widersprüchlich. Laden Sie das Projekt erneut.'); };
export function validDependencyNode(value: unknown): DependencyTask {
  if (!recordObject(value) || !recordId(value['id']) || !recordRevision(value['revision']) || !recordText(value['title'], 240, true)
    || typeof value['status'] !== 'string' || !Object.hasOwn(taskStatuses, value['status']) || typeof value['archived'] !== 'boolean'
    || !Array.isArray(value['predecessorTaskIds']) || value['predecessorTaskIds'].length > 16
    || !value['predecessorTaskIds'].every(recordId) || new Set(value['predecessorTaskIds']).size !== value['predecessorTaskIds'].length
    || value['predecessorTaskIds'].includes(value['id'])) fail();
  return value as unknown as DependencyTask;
}
export function validDependencyGraph(value: unknown, tenant: string, project: string): DependencyGraph {
  if (!recordObject(value) || value['tenantId'] !== tenant || value['projectId'] !== project || !recordId(project)
    || !recordText(value['projectName'], 240, true) || !Array.isArray(value['tasks']) || value['tasks'].length > 1000) fail();
  const tasks = (value as Record<string, unknown>)['tasks'] as unknown[];
  const nodes = tasks.map(validDependencyNode), byId = new Map(nodes.map(node => [node.id, node]));
  if (byId.size !== nodes.length) fail();
  const indegree = new Map<string, number>(), following = new Map(nodes.map(node => [node.id, [] as string[]]));
  for (const node of nodes) {
    indegree.set(node.id, node.predecessorTaskIds.length);
    for (const id of node.predecessorTaskIds) {
      const predecessor = byId.get(id);
      if (!predecessor || predecessor.archived || ['InProgress', 'Done'].includes(node.status) && predecessor.status !== 'Done') fail();
      following.get(id)!.push(node.id);
    }
  }
  const ready = nodes.filter(node => indegree.get(node.id) === 0).map(node => node.id); let count = 0;
  for (let index = 0; index < ready.length; index++) {
    count++;
    for (const next of following.get(ready[index])!) { indegree.set(next, indegree.get(next)! - 1); if (indegree.get(next) === 0) ready.push(next); }
  }
  if (count !== nodes.length) fail();
  return { tenantId: tenant, projectId: project, projectName: String((value as Record<string, unknown>)['projectName']), tasks: nodes };
}
export function blockedBy(graph: DependencyGraph, task: DependencyTask): DependencyTask[] {
  return graph.tasks.filter(node => task.predecessorTaskIds.includes(node.id) && node.status !== 'Done');
}
export function wouldCreateTaskCycle(graph: DependencyGraph, taskId: string, predecessorId: string): boolean {
  const byId = new Map(graph.tasks.map(node => [node.id, node])); const pending = [predecessorId], seen = new Set<string>();
  while (pending.length) { const id = pending.pop()!; if (id === taskId) return true; if (seen.has(id)) continue; seen.add(id); pending.push(...(byId.get(id)?.predecessorTaskIds ?? [])); }
  return false;
}
export function validDependencyChange(value: unknown, before: DependencyTask, request: { predecessorTaskIds: string[] } | { status: DependencyTaskStatus }): DependencyTask {
  const next = validDependencyNode(value), expectedIds = 'predecessorTaskIds' in request ? request.predecessorTaskIds : before.predecessorTaskIds;
  const expectedStatus = 'status' in request ? request.status : before.status;
  if (next.id !== before.id || next.revision !== before.revision + 1 || next.title !== before.title || next.archived !== before.archived
    || next.status !== expectedStatus || [...next.predecessorTaskIds].sort().join(',') !== [...expectedIds].sort().join(',')) fail();
  return next;
}
