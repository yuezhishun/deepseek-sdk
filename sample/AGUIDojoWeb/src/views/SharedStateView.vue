<script setup lang="ts">
import CopilotRuntime from "@/components/copilot/CopilotRuntime.vue";
import MobileChatDrawer from "@/components/copilot/MobileChatDrawer.vue";
import SidebarPanel from "@/components/copilot/SidebarPanel.vue";
import RecipeEditor from "@/components/recipe/RecipeEditor.vue";
import { useMobileView } from "@/composables/useMobileView";
import { useUrlParams } from "@/composables/useUrlParams";
import { AGENT_IDS } from "@/lib/agui";

const { isMobile } = useMobileView();
const { chatDefaultOpen } = useUrlParams();
</script>

<template>
  <CopilotRuntime :agent-id="AGENT_IDS.sharedState">
    <div class="flex h-full min-h-0 w-full items-center justify-center overflow-hidden">
      <RecipeEditor />
      <MobileChatDrawer
        v-if="isMobile"
        :agent-id="AGENT_IDS.sharedState"
        title="AI Recipe Assistant"
        description="Ask me to craft recipes"
      />
      <SidebarPanel
        v-else
        :agent-id="AGENT_IDS.sharedState"
        :default-open="chatDefaultOpen"
        title="AI Recipe Assistant"
      />
    </div>
  </CopilotRuntime>
</template>
