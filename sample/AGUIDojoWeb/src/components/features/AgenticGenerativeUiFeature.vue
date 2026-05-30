<script setup lang="ts">
import { computed } from "vue";
import {
  CopilotChat,
  CopilotChatMessageView,
  useAgent,
  useConfigureSuggestions,
  UseAgentUpdate,
} from "@copilotkit/vue/v2";
import TaskProgress, { type TaskProgressStep } from "@/components/task-progress/TaskProgress.vue";
import { AGENT_IDS } from "@/lib/agui";

interface AgentState {
  steps: TaskProgressStep[];
}

const { agent } = useAgent({
  agentId: AGENT_IDS.agenticGenerativeUi,
  updates: [UseAgentUpdate.OnStateChanged],
});
const steps = computed(() => ((agent.value?.state as AgentState | undefined)?.steps ?? []));

useConfigureSuggestions({
  suggestions: [
    { title: "Simple plan", message: "Please build a plan to go to mars in 5 steps." },
    { title: "Complex plan", message: "Please build a plan to go to make pizza in 10 steps." },
  ],
  available: "always",
});
</script>

<template>
  <div data-testid="copilot-chat" class="flex h-full min-h-0 w-full items-center justify-center">
    <div class="h-full w-full rounded-lg md:h-8/10 md:w-8/10">
      <CopilotChat :agent-id="AGENT_IDS.agenticGenerativeUi" class="mx-auto h-full max-w-6xl rounded-2xl">
        <template #message-view="{ messages, isRunning }">
          <div data-testid="copilot-message-list" class="flex min-h-0 flex-1 flex-col">
            <CopilotChatMessageView :messages="messages" :is-running="isRunning" />
            <div v-if="steps.length > 0" class="my-4">
              <TaskProgress :steps="steps" />
            </div>
          </div>
        </template>
      </CopilotChat>
    </div>
  </div>
</template>
