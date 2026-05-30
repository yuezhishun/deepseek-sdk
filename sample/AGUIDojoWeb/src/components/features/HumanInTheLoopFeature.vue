<script setup lang="ts">
import { h } from "vue";
import { z } from "zod";
import { CopilotChat, CopilotChatConfigurationProvider, useConfigureSuggestions, useHumanInTheLoop, useInterrupt } from "@copilotkit/vue/v2";
import StepCard from "@/components/steps/StepCard.vue";
import type { Step } from "@/components/steps/types";
import { AGENT_IDS } from "@/lib/agui";

function normalizeSteps(value: unknown): Step[] {
  if (!Array.isArray(value)) return [];
  return value.map((step: any) => ({
    description: typeof step === "string" ? step : step.description || "",
    status: typeof step === "object" && step.status ? step.status : "enabled",
  }));
}

useConfigureSuggestions({
  suggestions: [
    { title: "Simple plan", message: "Please plan a trip to mars in 5 steps." },
    { title: "Complex plan", message: "Please plan a pasta dish in 10 steps." },
  ],
  available: "always",
});
useInterrupt<{ steps: Step[] }>({
  agentId: AGENT_IDS.humanInTheLoop,
  renderInChat: true,
});
useHumanInTheLoop({
  agentId: AGENT_IDS.humanInTheLoop,
  name: "generate_task_steps",
  description: "Generates a list of steps for the user to perform",
  parameters: z.object({
    steps: z.array(z.object({ description: z.string(), status: z.enum(["enabled", "disabled", "executing"]) })),
  }),
  render: ({ args, respond, status }: any) =>
    Array.isArray(args?.steps) && args.steps.length > 0
      ? h(StepCard, { steps: args.steps, respond, status, mode: "confirm" })
      : null,
});
</script>

<template>
  <CopilotChatConfigurationProvider :agent-id="AGENT_IDS.humanInTheLoop">
    <div data-testid="copilot-chat" class="flex h-full min-h-0 w-full items-center justify-center">
      <div class="h-full w-full rounded-lg md:h-8/10 md:w-8/10">
        <CopilotChat :agent-id="AGENT_IDS.humanInTheLoop" class="mx-auto h-full max-w-6xl rounded-2xl">
          <template #interrupt="{ event, resolve }">
            <StepCard
              :steps="normalizeSteps((event.value as any)?.steps)"
              mode="perform"
              :respond="resolve"
            />
          </template>
        </CopilotChat>
      </div>
    </div>
  </CopilotChatConfigurationProvider>
</template>
