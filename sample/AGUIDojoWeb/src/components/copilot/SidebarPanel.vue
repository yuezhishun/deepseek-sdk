<script setup lang="ts">
import { CopilotSidebar } from "@copilotkit/vue/v2";
import { computed } from "vue";
import type { AgentId } from "@/lib/agui";

const props = defineProps<{
  agentId: AgentId;
  defaultOpen?: boolean;
  title: string;
}>();

const chatLabels = computed(() => ({ modalHeaderTitle: props.title } as any));
</script>

<template>
  <div data-testid="shared-state-chat-panel" class="hidden h-full md:block">
    <CopilotSidebar
      :agent-id="props.agentId"
      :default-open="props.defaultOpen"
      :labels="chatLabels"
      class="h-full"
    />
  </div>
</template>
