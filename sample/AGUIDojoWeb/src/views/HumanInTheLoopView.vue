<script setup lang="ts">
import CopilotRuntime from "@/components/copilot/CopilotRuntime.vue";
import HumanInTheLoopFeature from "@/components/features/HumanInTheLoopFeature.vue";
import { AGENT_IDS } from "@/lib/agui";
</script>

<template>
  <CopilotRuntime :agent-id="AGENT_IDS.humanInTheLoop">
    <HumanInTheLoopFeature />
  </CopilotRuntime>
</template>
