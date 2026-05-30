<script setup lang="ts">
import CopilotRuntime from "@/components/copilot/CopilotRuntime.vue";
import ToolBasedGenerativeUiFeature from "@/components/features/ToolBasedGenerativeUiFeature.vue";
import { AGENT_IDS } from "@/lib/agui";
</script>

<template>
  <CopilotRuntime :agent-id="AGENT_IDS.toolBasedGenerativeUi">
    <ToolBasedGenerativeUiFeature />
  </CopilotRuntime>
</template>
