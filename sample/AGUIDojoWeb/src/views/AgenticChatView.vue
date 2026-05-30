<script setup lang="ts">
import CopilotRuntime from "@/components/copilot/CopilotRuntime.vue";
import AgenticChatFeature from "@/components/features/AgenticChatFeature.vue";
import { AGENT_IDS } from "@/lib/agui";
</script>

<template>
  <CopilotRuntime :agent-id="AGENT_IDS.agenticChat">
    <AgenticChatFeature />
  </CopilotRuntime>
</template>
