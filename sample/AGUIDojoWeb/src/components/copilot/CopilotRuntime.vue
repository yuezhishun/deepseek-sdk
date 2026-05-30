<script setup lang="ts">
import { CopilotKitProvider } from "@copilotkit/vue/v2";
import type { AgentId } from "@/lib/agui";
import { createAgentRecord } from "@/lib/agui";
import { computed } from "vue";

const props = defineProps<{
  agentId: AgentId;
}>();

const agents = computed(() => createAgentRecord(props.agentId));
</script>

<template>
  <CopilotKitProvider :agents__unsafe_dev_only="agents" :show-dev-console="false">
    <slot />
  </CopilotKitProvider>
</template>
