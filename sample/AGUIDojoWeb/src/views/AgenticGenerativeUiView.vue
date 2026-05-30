<script setup lang="ts">
import CopilotRuntime from "@/components/copilot/CopilotRuntime.vue";
import AgenticGenerativeUiFeature from "@/components/features/AgenticGenerativeUiFeature.vue";
import { AGENT_IDS } from "@/lib/agui";
</script>

<template>
  <CopilotRuntime :agent-id="AGENT_IDS.agenticGenerativeUi">
    <AgenticGenerativeUiFeature />
  </CopilotRuntime>
</template>
