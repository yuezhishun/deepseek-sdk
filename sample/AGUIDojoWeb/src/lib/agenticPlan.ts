import type { TaskProgressStep } from "@/components/task-progress/TaskProgress.vue";

export interface PlanStep {
  description: string;
  status: "pending" | "completed";
}

export interface PlanState {
  steps: PlanStep[];
}

export interface PlanStateDeltaOperation {
  op?: string;
  path?: string;
  value?: unknown;
}

export function normalizePlanStatus(value: unknown): PlanStep["status"] | undefined {
  if (typeof value !== "string") {
    return undefined;
  }

  const normalized = value.toLowerCase();
  if (normalized === "pending" || normalized === "completed") {
    return normalized;
  }

  return undefined;
}

function normalizePlanStep(value: unknown): PlanStep | undefined {
  if (typeof value !== "object" || value === null) {
    return undefined;
  }

  const candidate = value as Record<string, unknown>;
  const status = normalizePlanStatus(candidate.status);
  if (typeof candidate.description !== "string" || status === undefined) {
    return undefined;
  }

  return {
    description: candidate.description,
    status,
  };
}

export function normalizePlanState(value: unknown): PlanState | undefined {
  if (typeof value !== "object" || value === null) {
    return undefined;
  }

  const candidate = value as Record<string, unknown>;
  if (!Array.isArray(candidate.steps)) {
    return undefined;
  }

  const steps = candidate.steps
    .map(normalizePlanStep)
    .filter((step): step is PlanStep => step !== undefined);

  if (steps.length !== candidate.steps.length) {
    return undefined;
  }

  return { steps };
}

export function getPlanStateFromStateEvent(value: unknown): PlanState | undefined {
  const normalizedPlanState = normalizePlanState(value);
  if (normalizedPlanState) {
    return normalizedPlanState;
  }

  if (typeof value !== "object" || value === null) {
    return undefined;
  }

  const candidate = value as Record<string, unknown>;
  return normalizePlanState(candidate.snapshot) ?? normalizePlanState(candidate.state);
}

export function isPlanStateSnapshot(value: unknown): value is PlanState {
  return normalizePlanState(value) !== undefined;
}

export function shouldRenderPlanStateAfterMessage(
  stateSnapshot: unknown,
  messageIndexInRun: number,
  numberOfMessagesInRun: number,
): stateSnapshot is PlanState {
  return (
    isPlanStateSnapshot(stateSnapshot)
    && stateSnapshot.steps.length > 0
    && messageIndexInRun === numberOfMessagesInRun - 1
  );
}

export function resolvePlanStateForMessageAfter(
  stateSnapshot: unknown,
  localState: unknown,
  messageIndexInRun: number,
  numberOfMessagesInRun: number,
): PlanState | undefined {
  const normalizedLocalState = getPlanStateFromStateEvent(localState);
  const normalizedSnapshot = getPlanStateFromStateEvent(stateSnapshot);

  if (
    normalizedLocalState
    && normalizedLocalState.steps.length > 0
    && messageIndexInRun === numberOfMessagesInRun - 1
  ) {
    return normalizedLocalState;
  }

  if (
    normalizedSnapshot
    && normalizedSnapshot.steps.length > 0
    && messageIndexInRun === numberOfMessagesInRun - 1
  ) {
    return normalizedSnapshot;
  }

  return undefined;
}

export function toTaskProgressSteps(planState: PlanState): TaskProgressStep[] {
  return planState.steps;
}

function extractTextFromUnknownContent(value: unknown): string {
  if (typeof value === "string") {
    return value;
  }

  if (Array.isArray(value)) {
    return value.map(extractTextFromUnknownContent).filter(Boolean).join("\n");
  }

  if (typeof value !== "object" || value === null) {
    return "";
  }

  const candidate = value as Record<string, unknown>;
  return extractTextFromUnknownContent(candidate.text ?? candidate.content ?? "");
}

export function isLikelyPlanSummaryText(value: unknown): boolean {
  const text = extractTextFromUnknownContent(value).trim();
  if (!text) {
    return false;
  }

  const lines = text
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);
  const tableLineCount = lines.filter((line) => line.includes("|")).length;
  const listLineCount = lines.filter((line) => /^([-*]|\d+\.)\s+/.test(line)).length;
  const statusTokenCount = (text.match(/\b(pending|completed)\b/gi) ?? []).length;

  return tableLineCount >= 2
    || (listLineCount >= 2 && statusTokenCount >= 2)
    || (lines.length >= 4 && statusTokenCount >= 3);
}

export function shouldHidePlanSummaryMessage(message: unknown, hasPlanState: boolean): boolean {
  if (!hasPlanState || typeof message !== "object" || message === null) {
    return false;
  }

  const candidate = message as Record<string, unknown>;
  return candidate.role === "assistant" && isLikelyPlanSummaryText(candidate.content ?? candidate);
}

export function applyPlanStateDelta(
  planState: PlanState,
  delta: PlanStateDeltaOperation[],
): PlanState {
  const nextSteps = planState.steps.map((step) => ({ ...step }));

  for (const operation of delta) {
    if (operation.op !== "replace" || typeof operation.path !== "string") {
      continue;
    }

    const match = /^\/steps\/(\d+)\/(description|status)$/.exec(operation.path);
    if (!match) {
      continue;
    }

    const stepIndex = Number.parseInt(match[1]!, 10);
    const field = match[2];
    const step = nextSteps[stepIndex];
    if (!step) {
      continue;
    }

    if (field === "description" && typeof operation.value === "string") {
      step.description = operation.value;
    }

    if (field === "status") {
      const status = normalizePlanStatus(operation.value);
      if (status) {
        step.status = status;
      }
    }
  }

  return { steps: nextSteps };
}
