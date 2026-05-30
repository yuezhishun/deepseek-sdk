<script setup lang="ts">
import { CopilotChat } from "@copilotkit/vue/v2";
import type { AgentId } from "@/lib/agui";

defineProps<{
  agentId: AgentId;
}>();
</script>

<template>
  <div data-testid="copilot-chat" class="flex h-full w-full items-center justify-center">
    <div class="h-full w-full rounded-lg md:h-8/10 md:w-8/10">
      <CopilotChat :agent-id="agentId" class="mx-auto h-full max-w-6xl rounded-2xl" />
    </div>
  </div>
</template>
