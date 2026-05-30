<script setup lang="ts">
import CopilotRuntime from "@/components/copilot/CopilotRuntime.vue";
import BackendToolRenderingFeature from "@/components/features/BackendToolRenderingFeature.vue";
import { AGENT_IDS } from "@/lib/agui";
</script>

<template>
  <CopilotRuntime :agent-id="AGENT_IDS.backendToolRendering">
    <BackendToolRenderingFeature />
  </CopilotRuntime>
</template>
