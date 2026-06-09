<script setup lang="ts">
import {
  CopilotChat,
  CopilotChatAssistantMessage,
  CopilotChatMessageView,
  useAgent,
  useConfigureSuggestions,
} from "@copilotkit/vue/v2";
import AgenticPlanMessageAfter from "@/components/features/AgenticPlanMessageAfter.vue";
import { AGENT_IDS } from "@/lib/agui";
import { getPlanStateFromStateEvent, shouldHidePlanSummaryMessage } from "@/lib/agenticPlan";

useConfigureSuggestions({
  suggestions: [
    { title: "Simple plan", message: "Please build a plan to go to mars in 5 steps." },
    { title: "Complex plan", message: "Please build a plan to go to make pizza in 10 steps." },
  ],
  available: "always",
});

const { agent } = useAgent({
  agentId: AGENT_IDS.agenticGenerativeUi,
});

function hasVisiblePlanState(): boolean {
  return (getPlanStateFromStateEvent(agent.value?.state)?.steps.length ?? 0) > 0;
}

function getLastUserMessageIndex(messages: readonly unknown[]): number {
  for (let index = messages.length - 1; index >= 0; index -= 1) {
    const candidate = messages[index] as { role?: unknown } | undefined;
    if (candidate?.role === "user") {
      return index;
    }
  }

  return -1;
}

function shouldHideAssistantMessage(message: unknown, messages: readonly unknown[]): boolean {
  if (!hasVisiblePlanState()) {
    return false;
  }

  const lastUserMessageIndex = getLastUserMessageIndex(messages);
  const messageIndex = messages.findIndex((candidate) => candidate === message);
  if (messageIndex === -1 || messageIndex <= lastUserMessageIndex) {
    return false;
  }

  return shouldHidePlanSummaryMessage(message, true);
}
</script>

<template>
  <div data-testid="copilot-chat" class="flex h-full min-h-0 w-full items-center justify-center">
    <div class="h-full w-full rounded-lg md:h-8/10 md:w-8/10">
      <CopilotChat :agent-id="AGENT_IDS.agenticGenerativeUi" class="mx-auto h-full max-w-6xl rounded-2xl">
        <template #message-view="{ messages, isRunning }">
          <div data-testid="copilot-message-list" class="flex min-h-0 flex-1 flex-col">
            <CopilotChatMessageView :messages="messages" :is-running="isRunning">
              <template #assistant-message="{ message, messages: slotMessages, isRunning: slotIsRunning }">
                <CopilotChatAssistantMessage
                  v-if="!shouldHideAssistantMessage(message, slotMessages)"
                  :message="message"
                  :messages="slotMessages"
                  :is-running="slotIsRunning"
                />
              </template>
              <template
                #message-after="{ messageIndex, stateSnapshot, messageIndexInRun, numberOfMessagesInRun, runId }"
              >
                <AgenticPlanMessageAfter
                  v-if="messageIndex === messages.length - 1"
                  :is-last-message="true"
                  :state-snapshot="stateSnapshot"
                  :message-index-in-run="messageIndexInRun"
                  :number-of-messages-in-run="numberOfMessagesInRun"
                  :run-id="runId"
                />
              </template>
            </CopilotChatMessageView>
          </div>
        </template>
      </CopilotChat>
    </div>
  </div>
</template>
