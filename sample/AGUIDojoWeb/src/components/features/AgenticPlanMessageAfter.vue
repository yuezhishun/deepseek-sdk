<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { useAgent, UseAgentUpdate } from "@copilotkit/vue/v2";
import type { AgentSubscriber } from "@ag-ui/client";
import TaskProgress from "@/components/task-progress/TaskProgress.vue";
import { AGENT_IDS } from "@/lib/agui";
import {
  applyPlanStateDelta,
  getPlanStateFromStateEvent,
  resolvePlanStateForMessageAfter,
  toTaskProgressSteps,
  type PlanState,
} from "@/lib/agenticPlan";

const props = defineProps<{
  isLastMessage: boolean;
  stateSnapshot: unknown;
  messageIndexInRun: number;
  numberOfMessagesInRun: number;
  runId: string;
}>();

const { agent } = useAgent({
  agentId: AGENT_IDS.agenticGenerativeUi,
  updates: [UseAgentUpdate.OnRunStatusChanged],
});

const plansByRun = shallowRef<Record<string, PlanState>>({});
const activeRunId = shallowRef<string | undefined>(props.runId);

function setPlanForRun(runId: string | undefined, planState: PlanState | undefined) {
  if (!runId || !planState) {
    return;
  }

  plansByRun.value = {
    ...plansByRun.value,
    [runId]: planState,
  };
}

function resetPlans(runId: string | undefined) {
  activeRunId.value = runId;
  plansByRun.value = {};
}

watch(
  () => [props.runId, props.stateSnapshot] as const,
  ([runId, stateSnapshot]) => {
    activeRunId.value = runId;

    if (!runId || plansByRun.value[runId]) {
      return;
    }

    setPlanForRun(runId, getPlanStateFromStateEvent(stateSnapshot));
  },
  { immediate: true },
);

watch(
  agent,
  (nextAgent, _previousAgent, onCleanup) => {
    if (!nextAgent) {
      return;
    }

    setPlanForRun(props.runId, plansByRun.value[props.runId] ?? getPlanStateFromStateEvent(nextAgent.state));

    const subscriber: AgentSubscriber = {
      onRunStartedEvent: ({ event, input }) => {
        resetPlans(event.runId ?? input.runId);
      },
      onStateSnapshotEvent: ({ event, state, input }) => {
        setPlanForRun(input.runId, getPlanStateFromStateEvent(event.snapshot) ?? getPlanStateFromStateEvent(state));
      },
      onStateDeltaEvent: ({ event, state, input }) => {
        const runId = input.runId ?? activeRunId.value ?? props.runId;
        const baselinePlanState = runId
          ? plansByRun.value[runId] ?? getPlanStateFromStateEvent(state)
          : getPlanStateFromStateEvent(state);

        if (!runId || !baselinePlanState) {
          return;
        }

        setPlanForRun(runId, applyPlanStateDelta(baselinePlanState, event.delta));
      },
      onRunFinishedEvent: ({ event, state, input }) => {
        const runId = event.runId ?? input.runId ?? activeRunId.value;
        if (!runId) {
          return;
        }

        setPlanForRun(runId, plansByRun.value[runId] ?? getPlanStateFromStateEvent(state));
      },
      onRunErrorEvent: ({ state, input }) => {
        setPlanForRun(input.runId ?? activeRunId.value, getPlanStateFromStateEvent(state));
      },
    };

    const subscription = nextAgent.subscribe(subscriber);
    onCleanup(() => subscription.unsubscribe());
  },
  { immediate: true },
);

const planState = computed(() =>
  props.isLastMessage
    ? resolvePlanStateForMessageAfter(
      props.stateSnapshot,
      plansByRun.value[props.runId],
      props.messageIndexInRun,
      props.numberOfMessagesInRun,
    )
    : undefined,
);
</script>

<template>
  <div v-if="planState" :key="runId" class="my-4">
    <TaskProgress :steps="toTaskProgressSteps(planState)" />
  </div>
</template>
