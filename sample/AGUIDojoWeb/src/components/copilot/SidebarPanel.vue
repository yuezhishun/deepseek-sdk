<script setup lang="ts">
import { CopilotSidebar } from "@copilotkit/vue/v2";
import type { AgentId } from "@/lib/agui";

defineProps<{
  agentId: AgentId;
  defaultOpen?: boolean;
  title: string;
}>();
</script>

<template>
  <CopilotSidebar
    :agent-id="agentId"
    :default-open="defaultOpen"
    :labels="{ modalHeaderTitle: title as any }"
  />
</template>
