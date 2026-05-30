<script setup lang="ts">
import { useConfigureSuggestions } from "@copilotkit/vue/v2";
import SidebarPanel from "@/components/copilot/SidebarPanel.vue";
import HaikuDisplay from "@/components/haiku/HaikuDisplay.vue";
import { useUrlParams } from "@/composables/useUrlParams";
import { AGENT_IDS } from "@/lib/agui";

const { chatDefaultOpen } = useUrlParams();

useConfigureSuggestions({
  suggestions: [
    { title: "Nature Haiku", message: "Write me a haiku about nature." },
    { title: "Ocean Haiku", message: "Create a haiku about the ocean." },
    { title: "Spring Haiku", message: "Generate a haiku about spring." },
  ],
  available: "always",
});
</script>

<template>
  <div class="h-full min-h-0 w-full">
    <SidebarPanel
      :agent-id="AGENT_IDS.toolBasedGenerativeUi"
      :default-open="chatDefaultOpen"
      title="Haiku Generator"
    />
    <HaikuDisplay />
  </div>
</template>
