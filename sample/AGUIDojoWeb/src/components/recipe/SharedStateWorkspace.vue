<script setup lang="ts">
import { CopilotChatConfigurationProvider } from "@copilotkit/vue/v2";
import MobileChatDrawer from "@/components/copilot/MobileChatDrawer.vue";
import SidebarPanel from "@/components/copilot/SidebarPanel.vue";
import { useMobileView } from "@/composables/useMobileView";
import { useUrlParams } from "@/composables/useUrlParams";
import { AGENT_IDS } from "@/lib/agui";
import RecipeEditor from "./RecipeEditor.vue";

const { isMobile } = useMobileView();
const { chatDefaultOpen } = useUrlParams();
</script>

<template>
  <CopilotChatConfigurationProvider :agent-id="AGENT_IDS.sharedState">
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
  </CopilotChatConfigurationProvider>
</template>
