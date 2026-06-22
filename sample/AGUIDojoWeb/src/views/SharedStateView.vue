<script setup lang="ts">
import CopilotRuntime from "@/components/copilot/CopilotRuntime.vue";
import SharedStateWorkspace from "@/components/recipe/SharedStateWorkspace.vue";
import { AGENT_IDS } from "@/lib/agui";
</script>

<template>
  <CopilotRuntime :agent-id="AGENT_IDS.sharedState">
    <SharedStateWorkspace />
  </CopilotRuntime>
</template>
